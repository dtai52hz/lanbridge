package io.github.dtai52hz.lanbridge;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import org.junit.Before;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowContentResolver;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.Arrays;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import android.os.CancellationSignal;
import android.content.res.AssetFileDescriptor;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk = 28)
public final class AndroidFileStorageTest {
    @Rule public TemporaryFolder temporary = new TemporaryFolder();
    private AndroidFileStorage storage;
    private static final byte[] PAYLOAD = "Một tệp tiếng Việt · 你好 · 📁".getBytes(StandardCharsets.UTF_8);
    @Before public void create() throws Exception { storage = new AndroidFileStorage(temporary.newFolder("private")); }
    private AndroidFileStorage.Receipt complete(String name) throws Exception {
        AndroidFileStorage.ReceiptDestination destination = storage.destination(name);
        try (java.io.OutputStream output = destination.open()) { output.write(PAYLOAD); }
        destination.finish(); return storage.receipts().get(0);
    }
    @Test public void verifiedFinishPersistsReceiptBeforeObserverAndSurvivesReload() throws Exception {
        AndroidFileStorage.ReceiptDestination destination = storage.destination("báo cáo.txt");
        try (java.io.OutputStream output = destination.open()) { output.write(PAYLOAD); }
        assertTrue("A partial file must not appear as a received receipt", storage.receipts().isEmpty());
        destination.finish();
        AndroidFileStorage reloaded = new AndroidFileStorage(storage.incomingDirectory().getParentFile());
        assertEquals(1, reloaded.receipts().size());
        assertEquals("báo cáo.txt", reloaded.receipts().get(0).name);
        assertArrayEquals(PAYLOAD, Files.readAllBytes(reloaded.receipts().get(0).file.toPath()));
    }
    @Test public void cancellationAfterFinishRollsBackPublishedFileAndIndex() throws Exception {
        AndroidFileStorage.ReceiptDestination destination = storage.destination("cancel.txt");
        try (java.io.OutputStream output = destination.open()) { output.write(PAYLOAD); }
        destination.finish(); File file = storage.receipts().get(0).file;
        destination.abort(); destination.abort();
        assertFalse(file.exists()); assertTrue(storage.receipts().isEmpty());
        assertTrue(new AndroidFileStorage(storage.incomingDirectory().getParentFile()).receipts().isEmpty());
    }
    @Test public void cancellationBeforeFinishCannotPublishOrLeavePartial() throws Exception {
        AndroidFileStorage.ReceiptDestination destination = storage.destination("partial.txt");
        try (java.io.OutputStream output = destination.open()) { output.write(PAYLOAD); }
        destination.abort();
        try { destination.finish(); fail("Cancelled destination was published"); } catch (IOException expected) { }
        assertTrue(storage.receipts().isEmpty());
        assertEquals(0, storage.incomingDirectory().listFiles().length);
    }
    @Test public void atomicBackupOnlyIndexRestoresVerifiedReceipts() throws Exception {
        AndroidFileStorage.Receipt receipt = complete("backup.txt");
        File index = new File(storage.incomingDirectory().getParentFile(), "received-index.json");
        File backup = new File(index.getPath() + ".bak"); assertTrue(index.renameTo(backup));
        AndroidFileStorage reloaded = new AndroidFileStorage(storage.incomingDirectory().getParentFile());
        assertEquals(1, reloaded.receipts().size()); assertEquals(receipt.id, reloaded.receipts().get(0).id);
        assertTrue(index.isFile()); assertArrayEquals(PAYLOAD, Files.readAllBytes(receipt.file.toPath()));
    }
    @Test public void oldActivityRollbackCannotOverwriteNewActivityReceipts() throws Exception {
        AndroidFileStorage.ReceiptDestination old = storage.destination("old-session.txt");
        try (java.io.OutputStream output = old.open()) { output.write(PAYLOAD); }
        old.finish();
        AndroidFileStorage nextActivity = new AndroidFileStorage(storage.incomingDirectory().getParentFile());
        AndroidFileStorage.ReceiptDestination fresh = nextActivity.destination("new-session.txt");
        try (java.io.OutputStream output = fresh.open()) { output.write(PAYLOAD); }
        fresh.finish();
        old.abort();
        AndroidFileStorage reloaded = new AndroidFileStorage(storage.incomingDirectory().getParentFile());
        assertEquals(1, reloaded.receipts().size()); assertEquals("new-session.txt", reloaded.receipts().get(0).name);
        assertEquals("Both Activity instances must observe the same current receipt snapshot", 1, storage.receipts().size());
    }
    @Test public void receiptListIsBoundedAndDeleteAllowsAnotherIncomingOffer() throws Exception {
        for (int i = 0; i < AndroidFileStorage.MAX_RECEIPTS; i++) complete("file-" + i + ".txt");
        try { storage.checkIncoming(0); fail("The receipt cap was ignored"); } catch (IOException expected) { assertTrue(expected.getMessage().contains("8")); }
        AndroidFileStorage.Receipt first = storage.receipts().get(0); storage.delete(first);
        assertFalse(first.file.exists()); storage.checkIncoming(0);
        assertEquals(7, new AndroidFileStorage(storage.incomingDirectory().getParentFile()).receipts().size());
    }
    @Test public void pathsAndPartialFilesCannotBecomeExportableReceipts() throws Exception {
        File outside = temporary.newFile("outside.txt"); Files.write(outside.toPath(), PAYLOAD);
        try { storage.addVerified("outside.txt", outside); fail("An outside file entered the receipt index"); } catch (IOException expected) { }
        File partial = new File(storage.incomingDirectory(), "unfinished.partial"); Files.write(partial.toPath(), PAYLOAD);
        try { storage.addVerified("unfinished.txt", partial); fail("A partial file entered the receipt index"); } catch (IOException expected) { }
        assertTrue(storage.receipts().isEmpty());
        assertEquals("tep-CON.txt", AndroidFileStorage.portableName("CON.txt"));
        assertEquals("a_b_c.txt", AndroidFileStorage.portableName("a/b:c.txt"));
    }
    private TestDocumentsProvider provider(boolean unknownSize) throws Exception {
        TestDocumentsProvider provider = new TestDocumentsProvider();
        provider.source = temporary.newFile("source-" + unknownSize + ".txt"); Files.write(provider.source.toPath(), PAYLOAD);
        provider.output = temporary.newFile("destination-" + unknownSize + ".txt"); provider.unknownSize = unknownSize;
        android.content.pm.ProviderInfo info = new android.content.pm.ProviderInfo(); info.authority = "lanbridge.documents"; info.exported = true;
        provider.attachInfo(RuntimeEnvironment.getApplication(), info);
        ShadowContentResolver.registerProviderInternal("lanbridge.documents", provider); return provider;
    }
    @Test public void knownSizeSafSourceIsLazyAndOpensOnlyWhenFileProtocolNeedsIt() throws Exception {
        TestDocumentsProvider provider = provider(false);
        try (AndroidFileStorage.PreparedSource source = storage.prepare(RuntimeEnvironment.getApplication().getContentResolver(),
                Uri.parse("content://lanbridge.documents/source"), new AtomicBoolean(), done -> { })) {
            assertEquals(PAYLOAD.length, source.size()); assertEquals("nguồn-tiếng-Việt.txt", source.name());
            assertEquals("Metadata lookup must not read an entire provider file", 0, provider.reads);
            try (InputStream input = source.open()) { byte[] bytes = new byte[PAYLOAD.length]; assertEquals(bytes.length, input.read(bytes)); assertArrayEquals(PAYLOAD, bytes); }
            assertEquals(1, provider.reads);
        }
    }
    @Test public void providerReservedAndMalformedUnicodeNamesBecomeValidWireNames() throws Exception {
        TestDocumentsProvider provider = provider(false);
        for (String name : new String[] { "CON .txt", "LPT1  .bin", "bad\ud800name.txt", "bad\udc00.txt" }) {
            provider.displayName = name;
            try (AndroidFileStorage.PreparedSource source = storage.prepare(RuntimeEnvironment.getApplication().getContentResolver(),
                    Uri.parse("content://lanbridge.documents/source"), new AtomicBoolean(), done -> { })) {
                assertEquals(source.name(), io.github.dtai52hz.lanbridge.net.FileTransferClient.safeFileName(source.name()));
                assertFalse(source.name().contains("\ud800")); assertFalse(source.name().contains("\udc00"));
            }
        }
        assertEquals("tep-CON .txt", AndroidFileStorage.portableName("CON .txt"));
        assertEquals(0, provider.reads);
    }
    @Test public void unknownSizeSafSourceStagesBoundedAndCleansItsPrivateCopy() throws Exception {
        TestDocumentsProvider provider = provider(true);
        AndroidFileStorage.PreparedSource source = storage.prepare(RuntimeEnvironment.getApplication().getContentResolver(),
            Uri.parse("content://lanbridge.documents/source"), new AtomicBoolean(), done -> { });
        assertEquals(PAYLOAD.length, source.size()); assertEquals(1, provider.reads);
        File staged = source.staged; assertTrue(staged.isFile());
        try (InputStream input = source.open()) { byte[] bytes = new byte[PAYLOAD.length]; assertEquals(bytes.length, input.read(bytes)); assertArrayEquals(PAYLOAD, bytes); }
        assertEquals("Sending the staged source must not reopen a cloud provider", 1, provider.reads);
        source.close();
        for (int i = 0; i < 1000 && staged.exists(); i++) Thread.sleep(2);
        assertFalse(staged.exists());
    }
    @Test public void exportRequiresVerifiedReceiptAndLeavesPrivateOriginalForRetry() throws Exception {
        TestDocumentsProvider provider = provider(false);
        AndroidFileStorage.Receipt receipt = complete("nhận.txt");
        AndroidFileStorage.export(RuntimeEnvironment.getApplication().getContentResolver(), receipt,
            Uri.parse("content://lanbridge.documents/destination"), new AtomicBoolean());
        assertArrayEquals(PAYLOAD, Files.readAllBytes(provider.output.toPath()));
        assertArrayEquals(PAYLOAD, Files.readAllBytes(receipt.file.toPath()));
        try {
            AndroidFileStorage.export(RuntimeEnvironment.getApplication().getContentResolver(), receipt,
                Uri.parse("content://lanbridge.documents/destination"), new AtomicBoolean(true)); fail("Cancelled export succeeded");
        } catch (IOException expected) { }
        assertTrue("A failed/cancelled export may not delete the verified private receipt", receipt.file.isFile());
    }
    @Test public void rejectedPickerSchemesCannotReadOrOverwritePrivateFiles() throws Exception {
        AndroidFileStorage.Receipt receipt = complete("private.txt");
        File index = new File(storage.incomingDirectory().getParentFile(), "received-index.json");
        byte[] indexBefore = Files.readAllBytes(index.toPath());
        for (Uri uri : new Uri[] { Uri.fromFile(index), Uri.parse("android.resource://package/file"), Uri.parse("relative/path"), Uri.parse("content:///missing-authority") }) {
            try { storage.prepare(RuntimeEnvironment.getApplication().getContentResolver(), uri, new AtomicBoolean(), done -> { }); fail("Non-SAF source accepted"); }
            catch (IOException expected) { }
            try { AndroidFileStorage.export(RuntimeEnvironment.getApplication().getContentResolver(), receipt, uri, new AtomicBoolean()); fail("Non-SAF target accepted"); }
            catch (IOException expected) { }
            assertArrayEquals(indexBefore, Files.readAllBytes(index.toPath()));
            assertArrayEquals(PAYLOAD, Files.readAllBytes(receipt.file.toPath()));
        }
    }
    @Test public void cancellingOwnedProviderStreamClosesItAndNextOperationSucceeds() throws Exception {
        TestDocumentsProvider provider = provider(true); provider.blockReads = true;
        AndroidFileStorage.DocumentOperation operation = new AndroidFileStorage.DocumentOperation();
        // ContentResolver wraps provider descriptors in a new AssetFileDescriptor;
        // obtain the synthetic blocking stream directly to exercise the operation's
        // ownership/close contract without depending on Robolectric's OS pipe emulation.
        AssetFileDescriptor descriptor = operation.track(provider.openAssetFile(Uri.parse("content://lanbridge.documents/source"), "r", operation.signal));
        InputStream blocked = operation.track(descriptor.createInputStream());
        CompletableFuture<Boolean> pending = CompletableFuture.supplyAsync(() -> {
            try { blocked.read(new byte[16]); return false; } catch (IOException expected) { return true; }
        });
        assertTrue(provider.readEntered.await(5, TimeUnit.SECONDS));
        long started = System.nanoTime(); operation.cancel();
        assertTrue("Cancelling a provider must not block the UI caller", System.nanoTime() - started < TimeUnit.MILLISECONDS.toNanos(250));
        assertTrue(pending.get(5, TimeUnit.SECONDS)); assertTrue(provider.blockedClosed.get());
        provider.blockReads = false;
        try (AndroidFileStorage.PreparedSource next = storage.prepare(RuntimeEnvironment.getApplication().getContentResolver(),
            Uri.parse("content://lanbridge.documents/source"), new AndroidFileStorage.DocumentOperation(), done -> { })) {
            assertEquals(PAYLOAD.length, next.size());
        }
        assertArrayEquals("Cancelling a read must never modify/delete the picked original", PAYLOAD, Files.readAllBytes(provider.source.toPath()));
    }
    @Test public void cancelledBlockedWriteOwnsSameUriUntilCleanupThenNewSaveIsSafe() throws Exception {
        Uri uri = Uri.parse("content://lanbridge.documents/document/reused-name");
        AndroidFileStorage.DocumentTarget old = AndroidFileStorage.DocumentTarget.acquire(uri); assertNotNull(old);
        AndroidFileStorage.DocumentOperation cancellation = new AndroidFileStorage.DocumentOperation();
        CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
        File outputFile = temporary.newFile("reused-output.txt");
        java.io.OutputStream blocked = cancellation.track(new java.io.OutputStream() {
            public void write(int value) throws IOException {
                entered.countDown();
                try { if (!release.await(5, TimeUnit.SECONDS)) throw new IOException("Fake writer timed out"); }
                catch (InterruptedException error) { Thread.currentThread().interrupt(); throw new IOException(error); }
                throw new IOException("Cancelled provider write");
            }
            public void close() { /* An uncooperative provider does not release its write yet. */ }
        });
        CompletableFuture<Void> pending = CompletableFuture.runAsync(() -> {
            try { blocked.write(1); }
            catch (IOException expected) { if (old.owns()) outputFile.delete(); }
            finally { old.close(); }
        });
        assertTrue(entered.await(5, TimeUnit.SECONDS)); cancellation.cancel();
        assertNull("A new save may not reuse a URI while an old write/cleanup still owns it", AndroidFileStorage.DocumentTarget.acquire(uri));
        release.countDown(); pending.get(5, TimeUnit.SECONDS);
        try (AndroidFileStorage.DocumentTarget fresh = AndroidFileStorage.DocumentTarget.acquire(uri)) {
            assertNotNull(fresh); Files.write(outputFile.toPath(), PAYLOAD);
            assertFalse("The old operation may no longer delete the newly reused URI", old.owns());
            old.close(); assertTrue(fresh.owns());
            assertArrayEquals(PAYLOAD, Files.readAllBytes(outputFile.toPath()));
        }
    }
    public static final class TestDocumentsProvider extends ContentProvider {
        File source, output; boolean unknownSize, blockReads; int reads; String displayName = "nguồn-tiếng-Việt.txt";
        final CountDownLatch readEntered = new CountDownLatch(1), releaseRead = new CountDownLatch(1);
        final AtomicBoolean blockedClosed = new AtomicBoolean();
        public boolean onCreate() { return true; }
        public Cursor query(Uri uri, String[] projection, String selection, String[] args, String order) {
            MatrixCursor cursor = new MatrixCursor(new String[] { OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE });
            cursor.addRow(new Object[] { displayName, unknownSize ? null : source.length() }); return cursor;
        }
        public ParcelFileDescriptor openFile(Uri uri, String mode) throws java.io.FileNotFoundException {
            if (uri.getLastPathSegment().equals("source")) { reads++; return ParcelFileDescriptor.open(source, ParcelFileDescriptor.MODE_READ_ONLY); }
            return ParcelFileDescriptor.open(output, ParcelFileDescriptor.MODE_WRITE_ONLY | ParcelFileDescriptor.MODE_TRUNCATE);
        }
        @Override public AssetFileDescriptor openAssetFile(Uri uri, String mode, CancellationSignal cancellation) throws java.io.FileNotFoundException {
            if (!blockReads || !"source".equals(uri.getLastPathSegment())) return super.openAssetFile(uri, mode, cancellation);
            reads++;
            return new AssetFileDescriptor(ParcelFileDescriptor.open(source, ParcelFileDescriptor.MODE_READ_ONLY), 0, AssetFileDescriptor.UNKNOWN_LENGTH) {
                @Override public FileInputStream createInputStream() throws IOException {
                    return new FileInputStream(source) {
                        @Override public int read(byte[] bytes) throws IOException { return read(bytes, 0, bytes.length); }
                        @Override public int read(byte[] bytes, int offset, int length) throws IOException {
                            readEntered.countDown();
                            try { if (!releaseRead.await(5, TimeUnit.SECONDS)) throw new IOException("Fake provider timed out"); }
                            catch (InterruptedException error) { Thread.currentThread().interrupt(); throw new IOException(error); }
                            throw new IOException("Fake provider stream closed");
                        }
                        @Override public void close() throws IOException { blockedClosed.set(true); releaseRead.countDown(); super.close(); }
                    };
                }
            };
        }
        @Override public AssetFileDescriptor openTypedAssetFile(Uri uri, String mime, android.os.Bundle options, CancellationSignal cancellation) throws java.io.FileNotFoundException {
            return openAssetFile(uri, "r", cancellation);
        }
        public String getType(Uri uri) { return "application/octet-stream"; }
        public Uri insert(Uri uri, ContentValues values) { throw new UnsupportedOperationException(); }
        public int delete(Uri uri, String selection, String[] args) { throw new UnsupportedOperationException(); }
        public int update(Uri uri, ContentValues values, String selection, String[] args) { throw new UnsupportedOperationException(); }
    }
}
