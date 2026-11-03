package io.github.dtai52hz.lanbridge.net;

import org.json.JSONObject;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Files;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

/** Actual C# FileTransfer counterpart. This helper only permits isolated loopback tests. */
final class FileInteropProbe {
    static void run(String mode, String code) throws Exception {
        PairingCode pairing = PairingCode.parse(code);
        if (!pairing.address.equals("127.0.0.1") || !mode.equals("device") && !mode.equals("code")) throw new IllegalArgumentException("Loopback only.");
        File directory = Files.createTempDirectory("lanbridge-java-file-interop-").toFile();
        AtomicReference<String> error = new AtomicReference<>(); CountDownLatch done = new CountDownLatch(1);
        AtomicReference<LanBridgeClient> ref = new AtomicReference<>(); AtomicBoolean started = new AtomicBoolean(), inputSent = new AtomicBoolean();
        AtomicBoolean received = new AtomicBoolean(), sent = new AtomicBoolean(), cancelled = new AtomicBoolean(), revoked = new AtomicBoolean();
        AtomicReference<FileTransferClient.StagingDestination> destination = new AtomicReference<>();
        LanBridgeClient client = new LanBridgeClient(new LanBridgeClient.Listener() {
            public void onFilesPermission(boolean allowed) { if (allowed && started.compareAndSet(false,true)) ref.get().files().send(source("phone-data.bin",2*1024*1024+17,13,true)); }
            public void onPacket(JSONObject packet) {
                if (!packet.optString("type").equals("chat")) return;
                if (packet.optString("text").equals("start-revoke")) {
                    if (!ref.get().files().send(source("revoke-from-phone.bin",2*1024*1024,41,true))) { error.set("Cannot start revoke case."); done.countDown(); }
                } else if (packet.optString("text").equals("files-interop-ok")) done.countDown();
            }
            public void onError(String reason) { error.set(reason); done.countDown(); }
        }); ref.set(client);
        client.files().setReceiveAllowed(true);
        client.files().setListener(new FileTransferClient.Listener() {
            public void onOffer(FileTransferClient.Offer offer) {
                try { FileTransferClient.StagingDestination staged = FileTransferClient.staging(directory,offer.name); if (offer.name.equals("desktop-data.bin")) destination.set(staged); offer.accept(staged); }
                catch (IOException exception) { error.set(exception.getMessage()); offer.reject(); done.countDown(); }
            }
            public void onProgress(String id,String name,long bytes,long total,boolean incoming) {
                if (!incoming && name.equals("phone-data.bin") && bytes>0 && inputSent.compareAndSet(false,true)) { ref.get().enter(); ref.get().moveTo(12345,54321); ref.get().text("file-transfer-input-ok"); }
                if (incoming && name.equals("cancel-from-pc.bin") && bytes>0) ref.get().files().cancel(id);
            }
            public void onCompleted(String id,String name,boolean incoming) {
                if (incoming && name.equals("desktop-data.bin")) {
                    try {
                        byte[] bytes=Files.readAllBytes(destination.get().file().toPath());
                        if(bytes.length!=8*FileTransferClient.CHUNK_SIZE+137) throw new IOException("Desktop file size mismatch.");
                        for(int i=0;i<bytes.length;i++)if(bytes[i]!=(byte)(i*17+29))throw new IOException("Desktop file bytes mismatch."); received.set(true);
                    } catch(IOException exception){error.set(exception.getMessage());done.countDown();}
                } else if(!incoming && name.equals("phone-data.bin")) sent.set(true);
            }
            public void onError(String id,String name,boolean incoming,String reason) {
                if(incoming && name.equals("cancel-from-pc.bin")) cancelled.set(true);
                else if(!incoming && name.equals("revoke-from-phone.bin")) { revoked.set(true);ref.get().text("revoke-error-observed"); }
                else {error.set(reason);done.countDown();}
            }
        });
        try {
            if(mode.equals("device"))client.connectDevice(new Device("test","PC",pairing.address,pairing.port,pairing.fingerprint,false,0),"Android file interop");
            else client.connectCode(code,"Android file interop");
            if(!done.await(30,TimeUnit.SECONDS))throw new AssertionError("File interop timed out.");
            if(error.get()!=null)throw new AssertionError(error.get());
            if(!received.get()||!sent.get()||!cancelled.get()||!revoked.get()||client.files().canSend())throw new AssertionError("Missing file/permission case.");
        } finally {
            client.close();
            File[] staged=directory.listFiles();if(staged!=null)for(File file:staged)Files.deleteIfExists(file.toPath());Files.deleteIfExists(directory.toPath());
        }
        System.out.println("PASS Java files -> actual C# FileTransfer: "+mode);
    }
    private static FileTransferClient.Source source(String name,int size,int seed,boolean slow) {
        return new FileTransferClient.Source() {
            public String name(){return name;}public long size(){return size;}
            public InputStream open(){return new InputStream(){int position;volatile boolean closed;
                public int read() throws IOException {if(closed)throw new IOException("closed");return position==size?-1:((position++*17+seed)&255);}
                public int read(byte[] data,int offset,int length)throws IOException {
                    if(closed)throw new IOException("closed");if(position==size)return -1;
                    if(slow)try{Thread.sleep(20);}catch(InterruptedException exception){Thread.currentThread().interrupt();throw new IOException(exception);}
                    int count=Math.min(length,size-position);for(int i=0;i<count;i++)data[offset+i]=(byte)(position++*17+seed);return count;
                }
                public void close(){closed=true;}
            };}
        };
    }
}
