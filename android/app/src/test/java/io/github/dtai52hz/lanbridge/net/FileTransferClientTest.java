package io.github.dtai52hz.lanbridge.net;

import org.json.JSONObject;
import org.junit.Test;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.Base64;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import static org.junit.Assert.*;

public class FileTransferClientTest {
    private static final String ID = "1".repeat(32);
    @Test public void portableNamesRejectTraversalReservedDevicesAndControlCharacters() {
        for (String name : new String[]{"", " ", "..", "../a", "x\\y", "a:b", "CON.txt", "LPT¹.log", "nul", "a.", "a ", "a\nb", "x\u2028/evil", "x".repeat(161)})
            assertThrows(name, IllegalArgumentException.class, () -> FileTransferClient.safeFileName(name));
        assertEquals("Tiếng Việt 😀.txt", FileTransferClient.safeFileName("Tiếng Việt 😀.txt"));
    }
    @Test public void concurrentBidirectionalTransfersStreamMultipleChunksAndVerifyBeforeCompletion() throws Exception {
        try (Pair pair = new Pair()) {
            byte[] a = content(480000, 3), b = content(290000, 7);
            MemoryDestination receivedA = new MemoryDestination(), receivedB = new MemoryDestination();
            CountDownLatch complete = new CountDownLatch(4); List<String> errors = new CopyOnWriteArrayList<>();
            pair.a.setListener(autoAccept(receivedB, complete, errors)); pair.b.setListener(autoAccept(receivedA, complete, errors));
            AtomicInteger opensA = new AtomicInteger(), opensB = new AtomicInteger();
            assertTrue(pair.a.send(source("phone.txt", a, opensA))); assertTrue(pair.b.send(source("desktop.txt", b, opensB)));
            assertTrue(complete.await(5, TimeUnit.SECONDS)); assertTrue(errors.toString(), errors.isEmpty());
            assertArrayEquals(a, receivedA.bytes.toByteArray()); assertArrayEquals(b, receivedB.bytes.toByteArray());
            assertTrue(receivedA.finished); assertTrue(receivedB.finished); assertEquals(1, opensA.get()); assertEquals(1, opensB.get());
            for (JSONObject packet : pair.aPackets) if (packet.optString("type").equals("file-chunk")) {
                assertTrue(Base64.getDecoder().decode(packet.getString("data")).length <= FileTransferClient.CHUNK_SIZE);
                assertEquals(0, packet.optLong("length")); // Existing manual desktop releases have no chunk ACK negotiation.
            }
        }
    }
    @Test public void corruptedHashAbortsPartialAndNeverAcknowledgesReceipt() throws Exception {
        try (Pair pair = new Pair()) {
            MemoryDestination destination = new MemoryDestination(); CountDownLatch failed = new CountDownLatch(1);
            pair.b.setListener(new FileTransferClient.Listener() {
                @Override public void onOffer(FileTransferClient.Offer offer) { offer.accept(destination); }
                @Override public void onError(String id,String name,boolean incoming,String message) { if (incoming) failed.countDown(); }
            });
            pair.tamperHash = true; assertTrue(pair.a.send(source("bad.bin", content(90000, 5), new AtomicInteger())));
            assertTrue(failed.await(5, TimeUnit.SECONDS)); assertTrue(destination.aborted.await(2, TimeUnit.SECONDS)); assertFalse(destination.finished);
            assertTrue(pair.bPackets.stream().noneMatch(p -> p.optString("type").equals("file-received")));
        }
    }
    @Test public void sourceChangingSizeFailsInsteadOfClaimingVerifiedSuccess() throws Exception {
        for (int declared : new int[]{2, 4}) try (Pair pair = new Pair()) {
            CountDownLatch failed = new CountDownLatch(1); MemoryDestination destination = new MemoryDestination();
            pair.b.setListener(new FileTransferClient.Listener() { @Override public void onOffer(FileTransferClient.Offer offer) { offer.accept(destination); } });
            pair.a.setListener(new FileTransferClient.Listener() { @Override public void onError(String id,String name,boolean incoming,String message) { failed.countDown(); } });
            assertTrue(pair.a.send(new FileTransferClient.Source() {
                public String name() { return "changed.bin"; } public long size() { return declared; }
                public InputStream open() { return new ByteArrayInputStream(new byte[]{1,2,3}); }
            }));
            assertTrue(failed.await(5,TimeUnit.SECONDS)); assertTrue(destination.aborted.await(2,TimeUnit.SECONDS)); assertFalse(destination.finished);
        }
    }
    @Test public void incomingRequiresExplicitOptInAndARevokedOfferCannotBeAcceptedLater() throws Exception {
        try (Pair pair = new Pair()) {
            pair.b.setReceiveAllowed(false); CountDownLatch refused = new CountDownLatch(1); AtomicInteger opens = new AtomicInteger();
            pair.a.setListener(new FileTransferClient.Listener() { @Override public void onError(String id,String name,boolean incoming,String message) { refused.countDown(); } });
            assertTrue(pair.a.send(source("private.bin", new byte[0], opens))); assertTrue(refused.await(5,TimeUnit.SECONDS)); assertEquals(0, opens.get());
        }
        List<JSONObject> responses = new CopyOnWriteArrayList<>();
        try (FileTransferClient receiver = new FileTransferClient((packet,bulk) -> { responses.add(packet); return CompletableFuture.completedFuture(null); })) {
            receiver.setRemoteAllowed(true); receiver.setReceiveAllowed(true);
            AtomicReference<FileTransferClient.Offer> pending = new AtomicReference<>(); CountDownLatch offered = new CountDownLatch(1);
            receiver.setListener(new FileTransferClient.Listener() { @Override public void onOffer(FileTransferClient.Offer offer) { pending.set(offer); offered.countDown(); } });
            receiver.handle(offer(ID, "pending.bin", 10)); assertTrue(offered.await(2,TimeUnit.SECONDS)); receiver.setReceiveAllowed(false);
            assertFalse(pending.get().accept(new MemoryDestination())); assertTrue(responses.stream().anyMatch(p -> p.optString("type").equals("file-accept") && !p.optBoolean("allow")));
        }
    }
    @Test public void malformedChunkAndTruncatedEndAbortWithoutBlockingProtocolReader() throws Exception {
        for (boolean oversized : new boolean[]{true,false}) {
            CountDownLatch accepted = new CountDownLatch(1), failed = new CountDownLatch(1); MemoryDestination destination = new MemoryDestination();
            try (FileTransferClient receiver = new FileTransferClient((packet,bulk) -> { if (packet.optString("type").equals("file-accept") && packet.optBoolean("allow")) accepted.countDown(); return CompletableFuture.completedFuture(null); })) {
                receiver.setRemoteAllowed(true); receiver.setReceiveAllowed(true);
                receiver.setListener(new FileTransferClient.Listener() {
                    public void onOffer(FileTransferClient.Offer offer) { offer.accept(destination); }
                    public void onError(String id,String name,boolean incoming,String message) { failed.countDown(); }
                });
                receiver.handle(offer(ID,"bad.bin",2)); assertTrue(accepted.await(2,TimeUnit.SECONDS));
                if (oversized) { JSONObject chunk=packet("file-chunk",ID); Wire.put(chunk,"data","A".repeat(65540)); receiver.handle(chunk); }
                else { JSONObject end=packet("file-end",ID); Wire.put(end,"text","00".repeat(32)); receiver.handle(end); }
                assertTrue(failed.await(2,TimeUnit.SECONDS)); assertTrue(destination.aborted.await(2,TimeUnit.SECONDS)); assertFalse(destination.finished);
            }
        }
    }
    @Test public void permissionRevokeDuringBlockedDiskWriteReturnsImmediatelyAndRollsBack() throws Exception {
        CountDownLatch accepted=new CountDownLatch(1), writing=new CountDownLatch(1), unblock=new CountDownLatch(1), aborted=new CountDownLatch(1);
        AtomicInteger finish=new AtomicInteger();
        FileTransferClient.Destination destination=new FileTransferClient.Destination() {
            public OutputStream open() { return new OutputStream() { public void write(int b) { } public void write(byte[] bytes) throws IOException { writing.countDown(); try { if (!unblock.await(3,TimeUnit.SECONDS)) throw new IOException("blocked"); } catch (InterruptedException e) { throw new IOException(e); } } }; }
            public void finish() { finish.incrementAndGet(); } public void abort() { aborted.countDown(); unblock.countDown(); }
        };
        try (FileTransferClient receiver=new FileTransferClient((packet,bulk) -> { if (packet.optBoolean("allow")) accepted.countDown(); return CompletableFuture.completedFuture(null); })) {
            receiver.setRemoteAllowed(true); receiver.setReceiveAllowed(true); receiver.setListener(new FileTransferClient.Listener() { public void onOffer(FileTransferClient.Offer offer) { offer.accept(destination); } });
            receiver.handle(offer(ID,"blocked.bin",1)); assertTrue(accepted.await(2,TimeUnit.SECONDS));
            JSONObject chunk=packet("file-chunk",ID); Wire.put(chunk,"data","AQ=="); receiver.handle(chunk); assertTrue(writing.await(2,TimeUnit.SECONDS));
            long started=System.nanoTime(); receiver.setRemoteAllowed(false); assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-started)<200);
            assertTrue(aborted.await(2,TimeUnit.SECONDS)); assertEquals(0,finish.get());
        } finally { unblock.countDown(); }
    }
    @Test public void bulkQueueGivesInputAndHeartbeatPriorityAndCompletesOnlyAfterWrite() throws Exception {
        OutboundQueue queue=new OutboundQueue(8);
        CompletableFuture<Void> bulk=queue.offerAwaited(packet("file-chunk",ID),true);
        assertTrue(queue.offer(Wire.packet("$pong"))); assertTrue(queue.offer(Wire.packet("key")));
        assertEquals("$pong",queue.takeItem().packet.optString("type")); assertEquals("key",queue.takeItem().packet.optString("type"));
        OutboundQueue.Item file=queue.takeItem(); assertEquals("file-chunk",file.packet.optString("type")); assertFalse(bulk.isDone());
        file.completion.complete(null); assertTrue(bulk.isDone());
        CompletableFuture<Void> cancelled=queue.offerAwaited(packet("file-chunk",ID),true); cancelled.cancel(false);
        assertFalse(queue.offerAwaited(packet("file-end",ID),true).isCompletedExceptionally()); queue.close();
    }
    @Test public void stagingPublishesUniqueVerifiedFilesAndNeverOverwritesAnExistingName() throws Exception {
        File directory=Files.createTempDirectory("lanbridge-file-test-").toFile();
        try {
            File existing=new File(directory,"photo.bin"); Files.write(existing.toPath(),new byte[]{9});
            FileTransferClient.StagingDestination first=FileTransferClient.staging(directory,"photo.bin");
            assertNull(first.file()); first.open().write(new byte[]{1,2}); first.finish();
            assertNotNull(first.file()); assertArrayEquals(new byte[]{1,2},Files.readAllBytes(first.file().toPath())); assertArrayEquals(new byte[]{9},Files.readAllBytes(existing.toPath()));
            FileTransferClient.StagingDestination second=FileTransferClient.staging(directory,"photo.bin"); second.open().write(3); second.abort(); assertNull(second.file());
            assertEquals(2,directory.listFiles().length); first.abort(); assertEquals(1,directory.listFiles().length);
        } finally { File[] files=directory.listFiles(); if(files!=null) for(File file:files) Files.deleteIfExists(file.toPath()); Files.deleteIfExists(directory.toPath()); }
    }
    @Test public void moderateSlowPrivateDiskAcceptsBurstWithoutLosingChunks() throws Exception {
        int chunks=24;byte[] bytes=content(FileTransferClient.CHUNK_SIZE,9);
        CountDownLatch accepted=new CountDownLatch(1),complete=new CountDownLatch(1);AtomicReference<String> failure=new AtomicReference<>();
        ByteArrayOutputStream written=new ByteArrayOutputStream();
        try(FileTransferClient receiver=new FileTransferClient((packet,bulk)->{if(packet.optBoolean("allow"))accepted.countDown();return CompletableFuture.completedFuture(null);})) {
            receiver.setRemoteAllowed(true);receiver.setReceiveAllowed(true);
            receiver.setListener(new FileTransferClient.Listener(){
                public void onOffer(FileTransferClient.Offer offer){offer.accept(slowDestination(written));}
                public void onCompleted(String id,String name,boolean incoming){complete.countDown();}
                public void onError(String id,String name,boolean incoming,String message){failure.set(message);complete.countDown();}
            });
            receiver.handle(offer(ID,"slow.bin",(long)chunks*bytes.length));assertTrue(accepted.await(2,TimeUnit.SECONDS));
            MessageDigest hash=MessageDigest.getInstance("SHA-256");
            for(int i=0;i<chunks;i++){JSONObject part=packet("file-chunk",ID);Wire.put(part,"data",Base64.getEncoder().encodeToString(bytes));receiver.handle(part);hash.update(bytes);}
            JSONObject end=packet("file-end",ID);Wire.put(end,"text",toHex(hash.digest()));receiver.handle(end);
            assertTrue(complete.await(5,TimeUnit.SECONDS));assertNull(failure.get());assertEquals((long)chunks*bytes.length,written.size());
        }
    }
    @Test public void validLongUnicodeDisplayNameNeverBecomesAnOverlongDiskComponent() throws Exception {
        File directory=Files.createTempDirectory("lanbridge-unicode-name-").toFile();
        try {
            String name="界".repeat(156)+".txt";assertEquals(160,name.length());
            FileTransferClient.StagingDestination destination=FileTransferClient.staging(directory,name);
            destination.open().write(7);destination.finish();assertNotNull(destination.file());
            assertTrue(destination.file().getName().getBytes(java.nio.charset.StandardCharsets.UTF_8).length<255);
            assertArrayEquals(new byte[]{7},Files.readAllBytes(destination.file().toPath()));destination.abort();
        } finally {File[] files=directory.listFiles();if(files!=null)for(File file:files)Files.deleteIfExists(file.toPath());Files.deleteIfExists(directory.toPath());}
    }
    @Test public void cancelledProviderIgnoringCloseDoesNotStrandAFreshSourceOrLeakOldChunks() throws Exception {
        for(boolean blockOpen:new boolean[]{true,false})try(Pair pair=new Pair()) {
            CountDownLatch blocked=new CountDownLatch(1),release=new CountDownLatch(1),freshDone=new CountDownLatch(2);
            MemoryDestination fresh=new MemoryDestination();String name="fresh-manifest.bin";
            pair.b.setListener(new FileTransferClient.Listener(){
                public void onOffer(FileTransferClient.Offer offer){offer.accept(offer.name.equals(name)?fresh:new MemoryDestination());}
                public void onCompleted(String id,String fileName,boolean incoming){if(fileName.equals(name))freshDone.countDown();}
            });
            pair.a.setListener(new FileTransferClient.Listener(){public void onCompleted(String id,String fileName,boolean incoming){if(fileName.equals(name))freshDone.countDown();}});
            try {
                assertTrue(pair.a.send(stuckSource("old-manifest.bin",blockOpen,blocked,release)));assertTrue(blocked.await(2,TimeUnit.SECONDS));
                String oldId=pair.aPackets.stream().filter(p->p.optString("type").equals("file-offer")).findFirst().get().optString("id");
                pair.a.cancel();assertTrue(pair.a.canSend());
                byte[] expected={11,22,33,44};assertTrue(pair.a.send(source(name,expected,new AtomicInteger())));
                assertTrue(freshDone.await(3,TimeUnit.SECONDS));assertArrayEquals(expected,fresh.bytes.toByteArray());
                release.countDown();assertTrue(awaitSenderIdle(pair.a));
                assertTrue(pair.aPackets.stream().noneMatch(p->p.optString("id").equals(oldId)&&(p.optString("type").equals("file-chunk")||p.optString("type").equals("file-end"))));
                int cancelIndex=-1,freshOfferIndex=-1;
                for(int i=0;i<pair.aPackets.size();i++){JSONObject p=pair.aPackets.get(i);if(p.optString("type").equals("file-cancel")&&p.optString("id").equals(oldId))cancelIndex=i;if(p.optString("type").equals("file-offer")&&p.optString("name").equals(name))freshOfferIndex=i;}
                assertTrue(cancelIndex>=0&&freshOfferIndex>cancelIndex);
            } finally {release.countDown();}
        }
    }
    @Test public void twoStuckProvidersRejectMoreWorkWithoutQueueingAndRecoverWhenReleased() throws Exception {
        try(Pair pair=new Pair()) {
            CountDownLatch first=new CountDownLatch(1),second=new CountDownLatch(1),release=new CountDownLatch(1),freshDone=new CountDownLatch(1);
            List<String> errors=new CopyOnWriteArrayList<>();AtomicInteger opened=new AtomicInteger();
            pair.a.setListener(new FileTransferClient.Listener(){public void onError(String id,String name,boolean incoming,String message){errors.add(message);}public void onCompleted(String id,String name,boolean incoming){if(name.equals("new.bin"))freshDone.countDown();}});
            pair.b.setListener(new FileTransferClient.Listener(){public void onOffer(FileTransferClient.Offer offer){offer.accept(new MemoryDestination());}});
            try {
                assertTrue(pair.a.send(stuckSource("first.bin",true,first,release)));assertTrue(first.await(2,TimeUnit.SECONDS));pair.a.cancel();
                assertTrue(pair.a.send(stuckSource("second.bin",true,second,release)));assertTrue(second.await(2,TimeUnit.SECONDS));pair.a.cancel();
                assertFalse(pair.a.canSend());assertFalse(pair.a.send(source("never-queued.bin",new byte[]{5},opened)));assertEquals(0,opened.get());
                assertTrue(errors.stream().anyMatch(message->message.contains("Nhà cung cấp")));
                release.countDown();assertTrue(awaitSenderIdle(pair.a));
                assertTrue(pair.a.send(source("new.bin",new byte[]{6,7},opened)));assertTrue(freshDone.await(3,TimeUnit.SECONDS));assertEquals(1,opened.get());
            } finally {release.countDown();}
        }
    }
    @Test public void cancellationBetweenBulkQueueingAndFutureAssignmentCancelsTheStaleFrame() throws Exception {
        AtomicReference<FileTransferClient> ref=new AtomicReference<>();CountDownLatch queued=new CountDownLatch(1),returnFuture=new CountDownLatch(1);
        CompletableFuture<Void> pendingWrite=new CompletableFuture<>();AtomicInteger ends=new AtomicInteger();
        try(FileTransferClient client=new FileTransferClient((packet,bulk)->{
            String type=packet.optString("type");
            if(type.equals("file-offer")){JSONObject accept=packet("file-accept",packet.optString("id"));Wire.put(accept,"allow",true);ref.get().handle(accept);}
            if(type.equals("file-chunk")){waitForRelease(queued,returnFuture);return pendingWrite;}
            if(type.equals("file-end"))ends.incrementAndGet();
            return CompletableFuture.completedFuture(null);
        })) {
            ref.set(client);client.setRemoteAllowed(true);
            try {
                assertTrue(client.send(source("race.bin",new byte[]{1},new AtomicInteger())));assertTrue(queued.await(2,TimeUnit.SECONDS));
                client.cancel();returnFuture.countDown();assertTrue(awaitSenderIdle(client));
                assertTrue(pendingWrite.isCancelled());assertEquals(0,ends.get());assertTrue(client.canSend());
            } finally{returnFuture.countDown();}
        }
    }
    private static FileTransferClient.Source stuckSource(String name,boolean blockOpen,CountDownLatch entered,CountDownLatch release) {
        return new FileTransferClient.Source(){
            public String name(){return name;}public long size(){return 1;}
            public InputStream open(){if(blockOpen)waitForRelease(entered,release);return new InputStream(){boolean read;
                public int read(){if(!blockOpen&&!read)waitForRelease(entered,release);if(read)return -1;read=true;return 99;}
                public void close(){/* An intentionally non-cooperative provider. */}
            };}
        };
    }
    private static void waitForRelease(CountDownLatch entered,CountDownLatch release) {
        entered.countDown();boolean done=false;while(!done)try{done=release.await(5,TimeUnit.SECONDS);if(!done)throw new AssertionError("Test provider was not released.");}catch(InterruptedException ignored){ }
    }
    private static boolean awaitSenderIdle(FileTransferClient client)throws Exception {
        java.lang.reflect.Field field=FileTransferClient.class.getDeclaredField("sender");field.setAccessible(true);
        java.util.concurrent.ThreadPoolExecutor pool=(java.util.concurrent.ThreadPoolExecutor)field.get(client);
        long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(2);
        while(pool.getActiveCount()!=0&&System.nanoTime()<deadline)Thread.sleep(1);
        return pool.getActiveCount()==0;
    }
    @Test public void sustainedDiskOverloadCancelsOnlyFileAndAllowsANewTransfer() throws Exception {
        String nextId="2".repeat(32);CountDownLatch accepted=new CountDownLatch(1),failed=new CountDownLatch(1),retryAccepted=new CountDownLatch(1),completed=new CountDownLatch(1);
        AtomicReference<String> message=new AtomicReference<>();
        try(FileTransferClient receiver=new FileTransferClient((packet,bulk)->{if(packet.optBoolean("allow")){if(packet.optString("id").equals(nextId))retryAccepted.countDown();else accepted.countDown();}return CompletableFuture.completedFuture(null);})) {
            receiver.setRemoteAllowed(true);receiver.setReceiveAllowed(true);
            receiver.setListener(new FileTransferClient.Listener(){
                public void onOffer(FileTransferClient.Offer offer){offer.accept(offer.id.equals(nextId)?new MemoryDestination():slowDestination(new ByteArrayOutputStream()));}
                public void onError(String id,String name,boolean incoming,String error){message.set(error);failed.countDown();}
                public void onCompleted(String id,String name,boolean incoming){completed.countDown();}
            });
            receiver.handle(offer(ID,"overload.bin",256L*FileTransferClient.CHUNK_SIZE));assertTrue(accepted.await(2,TimeUnit.SECONDS));
            String encoded=Base64.getEncoder().encodeToString(new byte[FileTransferClient.CHUNK_SIZE]);
            for(int i=0;i<256;i++){JSONObject part=packet("file-chunk",ID);Wire.put(part,"data",encoded);receiver.handle(part);}
            assertTrue(failed.await(3,TimeUnit.SECONDS));assertTrue(message.get().contains("đầy"));assertTrue(receiver.canSend());assertTrue(receiver.isReceiveAllowed());
            receiver.handle(offer(nextId,"retry.bin",0));assertTrue(retryAccepted.await(3,TimeUnit.SECONDS));
            JSONObject end=packet("file-end",nextId);Wire.put(end,"text",toHex(MessageDigest.getInstance("SHA-256").digest()));receiver.handle(end);
            assertTrue(completed.await(3,TimeUnit.SECONDS));
        }
    }
    private static FileTransferClient.Destination slowDestination(ByteArrayOutputStream bytes) {
        return new FileTransferClient.Destination(){
            public OutputStream open(){return new OutputStream(){public void write(int b){bytes.write(b);}public void write(byte[] data)throws IOException{try{Thread.sleep(5);}catch(InterruptedException e){throw new IOException(e);}bytes.write(data);}};}
            public void finish(){}public void abort(){}
        };
    }
    private static String toHex(byte[] bytes){StringBuilder result=new StringBuilder();for(byte value:bytes)result.append(String.format("%02X",value&255));return result.toString();}
    private static FileTransferClient.Listener autoAccept(MemoryDestination destination,CountDownLatch complete,List<String> errors) {
        return new FileTransferClient.Listener() {
            public void onOffer(FileTransferClient.Offer offer) { offer.accept(destination); }
            public void onCompleted(String id,String name,boolean incoming) { complete.countDown(); }
            public void onError(String id,String name,boolean incoming,String message) { errors.add(message); }
        };
    }
    private static final class Pair implements AutoCloseable {
        final List<JSONObject> aPackets=new CopyOnWriteArrayList<>(),bPackets=new CopyOnWriteArrayList<>();
        final FileTransferClient a,b; volatile boolean tamperHash;
        Pair() {
            AtomicReference<FileTransferClient> ar=new AtomicReference<>(),br=new AtomicReference<>();
            a=new FileTransferClient((packet,bulk) -> { aPackets.add(packet); if(tamperHash&&packet.optString("type").equals("file-end")) Wire.put(packet,"text","00".repeat(32)); br.get().handle(packet); return CompletableFuture.completedFuture(null); });
            b=new FileTransferClient((packet,bulk) -> { bPackets.add(packet); ar.get().handle(packet); return CompletableFuture.completedFuture(null); }); ar.set(a);br.set(b);
            a.setRemoteAllowed(true);b.setRemoteAllowed(true);a.setReceiveAllowed(true);b.setReceiveAllowed(true);
        }
        public void close() { a.close();b.close(); }
    }
    private static FileTransferClient.Source source(String name,byte[] bytes,AtomicInteger opens) {
        return new FileTransferClient.Source() { public String name(){return name;}public long size(){return bytes.length;}public InputStream open(){opens.incrementAndGet();return new ByteArrayInputStream(bytes){ @Override public synchronized int read(byte[] b,int off,int len){assertTrue(len<=FileTransferClient.CHUNK_SIZE);return super.read(b,off,len);} };} };
    }
    private static byte[] content(int length,int seed) { byte[] bytes=new byte[length];for(int i=0;i<length;i++)bytes[i]=(byte)(i*17+seed);return bytes; }
    private static JSONObject packet(String type,String id) { JSONObject packet=Wire.packet(type);Wire.put(packet,"id",id);return packet; }
    private static JSONObject offer(String id,String name,long length) { JSONObject packet=packet("file-offer",id);Wire.put(packet,"name",name);Wire.put(packet,"length",length);return packet; }
    private static final class MemoryDestination implements FileTransferClient.Destination {
        final ByteArrayOutputStream bytes=new ByteArrayOutputStream();final CountDownLatch aborted=new CountDownLatch(1);volatile boolean finished;
        public OutputStream open(){return bytes;}public void finish(){finished=true;}public void abort(){aborted.countDown();}
    }
}
