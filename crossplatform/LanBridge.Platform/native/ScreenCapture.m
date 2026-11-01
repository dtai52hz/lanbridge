// ScreenCaptureKit 14+ screenshot bridge. Called only from an approved capture worker.
// No files, shell tools, obsolete CGDisplayCreateImage APIs, audio or persistent stream.
#import <Cocoa/Cocoa.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <CoreGraphics/CoreGraphics.h>
#include <math.h>
#include <string.h>

@interface LBScreenResult : NSObject
@property(nonatomic, strong) SCShareableContent *content;
@property(nonatomic, strong) NSError *error;
@property(nonatomic) CGImageRef image;
@end
@implementation LBScreenResult
- (void)dealloc { if (_image) CGImageRelease(_image); }
@end

static void LBDrawDisplay(CGContextRef context, CGImageRef image, CGRect frame, CGRect desktop, int outputWidth, int outputHeight)
{
    double scaleX = (double)outputWidth / desktop.size.width, scaleY = (double)outputHeight / desktop.size.height;
    CGRect target = CGRectMake((frame.origin.x - desktop.origin.x) * scaleX,
        outputHeight - (frame.origin.y - desktop.origin.y + frame.size.height) * scaleY,
        frame.size.width * scaleX, frame.size.height * scaleY);
    CGContextDrawImage(context, target, image);
}

static void LBExport(CGContextRef context, void *pixels, int width, int height, int stride)
{
    // A CGImage bitmap context already stores scanlines in image (top-to-bottom) order,
    // although its drawing coordinates have a lower-left origin. Do not flip them again.
    const unsigned char *source = CGBitmapContextGetData(context);
    for (int row = 0; row < height; row++)
        memcpy((unsigned char *)pixels + row * stride, source + row * width * 4, width * 4);
}

// Native-only geometry/row-order check; no display, permission prompt or captured desktop.
__attribute__((visibility("default")))
int lanbridge_capture_layout_smoke(void)
{
    const unsigned char pattern[] = { 0,0,255,255, 0,255,0,255, 255,0,0,255, 255,255,255,255 };
    CGDataProviderRef provider = CGDataProviderCreateWithData(NULL, pattern, sizeof(pattern), NULL);
    CGColorSpaceRef colors = CGColorSpaceCreateDeviceRGB();
    CGImageRef image = CGImageCreate(2, 2, 8, 32, 8, colors, kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst,
        provider, NULL, false, kCGRenderingIntentDefault);
    CGContextRef context = CGBitmapContextCreate(NULL, 2, 4, 8, 8, colors,
        kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst);
    if (!provider || !image || !context) return 1;
    CGContextSetRGBFillColor(context, 0, 0, 0, 1);
    CGContextFillRect(context, CGRectMake(0, 0, 2, 4));
    LBDrawDisplay(context, image, CGRectMake(-2, -2, 2, 2), CGRectMake(-2, -2, 2, 4), 2, 4);
    unsigned char result[32]; LBExport(context, result, 2, 4, 8);
    int status = memcmp(pattern, result, sizeof(pattern)) == 0 ? 0 : 2;
    if (result[16] != 0 || result[17] != 0 || result[18] != 0) status |= 4;
    CGContextRelease(context); CGImageRelease(image); CGColorSpaceRelease(colors); CGDataProviderRelease(provider);
    return status;
}

__attribute__((visibility("default")))
int lanbridge_capture_bgra(int x, int y, int width, int height, void *pixels, int outputWidth, int outputHeight, int stride)
{
    @autoreleasepool {
        if (!pixels || width < 2 || height < 2 || outputWidth < 2 || outputWidth > 1280 || outputHeight < 2 || outputHeight > 800 || stride < outputWidth * 4) return 3;
        // The managed caller reaches this only after both the local screen-viewing grant and
        // the peer's explicit screen-request. Opening LanBridge never raises this prompt.
        if (!CGPreflightScreenCaptureAccess()) {
            CGRequestScreenCaptureAccess();
            if (!CGPreflightScreenCaptureAccess()) return 1;
        }
        dispatch_time_t deadline = dispatch_time(DISPATCH_TIME_NOW, 10 * NSEC_PER_SEC);
        LBScreenResult *share = [LBScreenResult new];
        dispatch_semaphore_t available = dispatch_semaphore_create(0);
        [SCShareableContent getShareableContentWithCompletionHandler:^(SCShareableContent *content, NSError *error) {
            share.content = content; share.error = error;
            dispatch_semaphore_signal(available);
        }];
        if (dispatch_semaphore_wait(available, deadline) != 0) return 2;
        if (share.error || !share.content) return 3;
        CGRect currentDesktop = CGRectNull;
        for (SCDisplay *display in share.content.displays)
            currentDesktop = CGRectUnion(currentDesktop, CGDisplayBounds(display.displayID));
        if (CGRectIsNull(currentDesktop) || fabs(currentDesktop.origin.x - x) > 0.5 || fabs(currentDesktop.origin.y - y) > 0.5
            || fabs(currentDesktop.size.width - width) > 0.5 || fabs(currentDesktop.size.height - height) > 0.5) return 4;

        CGColorSpaceRef colorSpace = CGColorSpaceCreateDeviceRGB();
        CGContextRef context = CGBitmapContextCreate(NULL, outputWidth, outputHeight, 8, outputWidth * 4, colorSpace,
            kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst);
        CGColorSpaceRelease(colorSpace);
        if (!context) return 3;
        CGContextSetRGBFillColor(context, 0, 0, 0, 1);
        CGContextFillRect(context, CGRectMake(0, 0, outputWidth, outputHeight));
        CGRect desktop = CGRectMake(x, y, width, height);
        double scaleX = (double)outputWidth / width, scaleY = (double)outputHeight / height;
        int captured = 0, status = 0;
        for (SCDisplay *display in share.content.displays) {
            CGRect frame = CGDisplayBounds(display.displayID);
            if (!CGRectIntersectsRect(desktop, frame)) continue;
            SCContentFilter *filter = [[SCContentFilter alloc] initWithDisplay:display excludingWindows:@[]];
            SCStreamConfiguration *configuration = [SCStreamConfiguration new];
            configuration.width = MAX(2, (NSInteger)llround(frame.size.width * scaleX));
            configuration.height = MAX(2, (NSInteger)llround(frame.size.height * scaleY));
            configuration.showsCursor = YES;
            LBScreenResult *snapshot = [LBScreenResult new];
            dispatch_semaphore_t ready = dispatch_semaphore_create(0);
            [SCScreenshotManager captureImageWithFilter:filter configuration:configuration completionHandler:^(CGImageRef image, NSError *error) {
                // Keep ownership in the result object, even if the caller has already timed out.
                snapshot.image = image ? CGImageRetain(image) : NULL;
                snapshot.error = error;
                dispatch_semaphore_signal(ready);
            }];
            if (dispatch_semaphore_wait(ready, deadline) != 0) { status = 2; break; }
            if (snapshot.error || !snapshot.image) { status = 3; break; }
            // CoreGraphics has a bottom-left drawing origin; the protocol desktop is top-left.
            LBDrawDisplay(context, snapshot.image, frame, desktop, outputWidth, outputHeight);
            captured++;
        }
        if (status == 0 && captured == 0) status = 3;
        if (status == 0) {
            // Export CGImage-ordered scanlines as top-to-bottom BGRA.
            LBExport(context, pixels, outputWidth, outputHeight, stride);
        }
        CGContextRelease(context);
        return status;
    }
}
