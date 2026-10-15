#import <AppKit/AppKit.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <CoreMedia/CoreMedia.h>
#import <ApplicationServices/ApplicationServices.h>
#include <stdint.h>

// C ABI: no Objective-C objects or borrowed pixel buffers escape into managed code.
@interface URCapture : NSObject<SCStreamOutput, SCStreamDelegate>
@property SCStream *stream;
@property NSLock *lock;
@property NSData *pixels;
@property uint32_t width;
@property uint32_t height;
@property BOOL failed;
@end
@implementation URCapture
- (instancetype)init { if ((self = [super init])) _lock = [NSLock new]; return self; }
- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error {
    [self.lock lock]; self.failed = YES; self.pixels = nil; [self.lock unlock];
}
- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sample ofType:(SCStreamOutputType)type {
    if (type != SCStreamOutputTypeScreen || !CMSampleBufferIsValid(sample)) return;
    NSArray *attachments = (__bridge NSArray *)CMSampleBufferGetSampleAttachmentsArray(sample, false);
    NSNumber *status = attachments.firstObject[SCStreamFrameInfoStatus];
    if (!status || status.integerValue != SCFrameStatusComplete) return;
    CVPixelBufferRef buffer = CMSampleBufferGetImageBuffer(sample);
    if (!buffer || CVPixelBufferLockBaseAddress(buffer, kCVPixelBufferLock_ReadOnly) != kCVReturnSuccess) return;
    size_t w = CVPixelBufferGetWidth(buffer), h = CVPixelBufferGetHeight(buffer), stride = CVPixelBufferGetBytesPerRow(buffer);
    if (w && h && w <= 16384 && h <= 16384 && w*h*4 <= 256*1024*1024 && stride >= w*4) {
        NSMutableData *copy = [NSMutableData dataWithLength:w*h*4];
        const uint8_t *source = CVPixelBufferGetBaseAddress(buffer);
        if (source) {
            for (size_t y=0; y<h; y++) memcpy((uint8_t *)copy.mutableBytes+y*w*4,source+y*stride,w*4);
            [self.lock lock]; self.pixels=copy; self.width=(uint32_t)w; self.height=(uint32_t)h; [self.lock unlock];
        }
    }
    CVPixelBufferUnlockBaseAddress(buffer,kCVPixelBufferLock_ReadOnly);
}
@end

int ur_permissions(int input, int prompt) {
    BOOL screen = CGPreflightScreenCaptureAccess();
    if (!screen && prompt) screen = CGRequestScreenCaptureAccess();
    NSDictionary *options = @{(__bridge NSString *)kAXTrustedCheckOptionPrompt: @(prompt != 0)};
    BOOL access = !input || AXIsProcessTrustedWithOptions((__bridge CFDictionaryRef)options);
    return (screen ? 1 : 0) | (access ? 2 : 0);
}
int ur_displays(uint32_t *ids, int capacity) {
    uint32_t count=0;
    if (capacity<1 || CGGetActiveDisplayList(capacity,ids,&count)!=kCGErrorSuccess) return -1;
    return (int)count;
}
int ur_refresh(uint32_t display) {
    CGDisplayModeRef mode=CGDisplayCopyDisplayMode(display); if (!mode) return 60;
    double rate=CGDisplayModeGetRefreshRate(mode); CGDisplayModeRelease(mode);
    return rate >= 1 && rate <= 500 ? (int)rate : 60;
}
void *ur_capture_open(uint32_t display, int cursor) {
    @autoreleasepool {
        if (!CGPreflightScreenCaptureAccess()) return NULL;
        dispatch_semaphore_t done=dispatch_semaphore_create(0);
        __block URCapture *capture=nil;
        [SCShareableContent getShareableContentExcludingDesktopWindows:NO onScreenWindowsOnly:YES completionHandler:^(SCShareableContent *content,NSError *error) {
            if (error) { dispatch_semaphore_signal(done); return; }
            for (SCDisplay *screen in content.displays) {
                if (screen.displayID!=display) continue;
                URCapture *candidate=[URCapture new];
                SCStreamConfiguration *config=[SCStreamConfiguration new];
                config.width=CGDisplayPixelsWide(display); config.height=CGDisplayPixelsHigh(display);
                config.pixelFormat=kCVPixelFormatType_32BGRA;
                config.minimumFrameInterval=CMTimeMake(1,ur_refresh(display)); config.queueDepth=3;
                config.showsCursor=cursor!=0;
                SCContentFilter *filter=[[SCContentFilter alloc] initWithDisplay:screen excludingWindows:@[]];
                candidate.stream=[[SCStream alloc] initWithFilter:filter configuration:config delegate:candidate];
                NSError *addError=nil;
                if (![candidate.stream addStreamOutput:candidate type:SCStreamOutputTypeScreen sampleHandlerQueue:dispatch_queue_create("uremote.capture",DISPATCH_QUEUE_SERIAL) error:&addError]) break;
                capture=candidate;
                break;
            }
            dispatch_semaphore_signal(done);
        }];
        // Async callbacks own their temporary objects even when the bounded wait expires.
        if (dispatch_semaphore_wait(done,dispatch_time(DISPATCH_TIME_NOW,10*NSEC_PER_SEC))!=0 || !capture) return NULL;
        URCapture *result=capture;
        dispatch_semaphore_t started=dispatch_semaphore_create(0);
        __block BOOL ok=NO;
        [result.stream startCaptureWithCompletionHandler:^(NSError *error) { ok=error==nil; dispatch_semaphore_signal(started); }];
        if (dispatch_semaphore_wait(started,dispatch_time(DISPATCH_TIME_NOW,10*NSEC_PER_SEC))!=0 || !ok) {
            [result.stream stopCaptureWithCompletionHandler:^(NSError *error) { (void)error; }]; return NULL;
        }
        return (__bridge_retained void *)result;
    }
}
int ur_capture_copy(void *handle, void *target, int capacity, uint32_t *width, uint32_t *height) {
    URCapture *capture=(__bridge URCapture *)handle;
    [capture.lock lock];
    *width=capture.width; *height=capture.height;
    int size=capture.failed ? -1 : (int)capture.pixels.length;
    if (target && size>0 && capacity>=size) memcpy(target,capture.pixels.bytes,size);
    [capture.lock unlock]; return size;
}
void ur_capture_close(void *handle) {
    URCapture *capture=(__bridge_transfer URCapture *)handle;
    [capture.stream stopCaptureWithCompletionHandler:^(NSError *error) { (void)error; }];
    [capture.lock lock]; capture.failed=YES; capture.pixels=nil; [capture.lock unlock];
}
void ur_key(int key, int down, uint64_t flags) {
    if (!AXIsProcessTrusted() || key<0 || key>127) return;
    CGEventRef event=CGEventCreateKeyboardEvent(NULL,(CGKeyCode)key,down!=0);
    if (event) { CGEventSetFlags(event,flags); CGEventPost(kCGHIDEventTap,event); CFRelease(event); }
}
void ur_text(const uint16_t *text, int length) {
    if (!AXIsProcessTrusted() || length<=0 || length>4096) return;
    for (int offset=0; offset<length;) {
        int count=MIN(16,length-offset);
        if (offset+count<length && text[offset+count-1]>=0xD800 && text[offset+count-1]<=0xDBFF) count--;
        for (int down=1; down>=0; down--) {
            CGEventRef event=CGEventCreateKeyboardEvent(NULL,0,down);
            if (event) { CGEventKeyboardSetUnicodeString(event,count,text+offset); CGEventPost(kCGHIDEventTap,event); CFRelease(event); }
        }
        offset+=count;
    }
}
void ur_pointer(uint32_t display, int action, double x, double y, int button, int dragging, uint64_t flags) {
    if (!AXIsProcessTrusted() || !CGDisplayIsActive(display)) return;
    CGEventRef current=CGEventCreate(NULL); if (!current) return;
    CGPoint location=CGEventGetLocation(current); CFRelease(current);
    CGEventRef event=NULL;
    if (action==0) {
        CGRect bounds=CGDisplayBounds(display);
        location=CGPointMake(bounds.origin.x+fmin(1,fmax(0,x))*fmax(0,bounds.size.width-1), bounds.origin.y+fmin(1,fmax(0,y))*fmax(0,bounds.size.height-1));
        CGEventType type=dragging<0?kCGEventMouseMoved:dragging==0?kCGEventLeftMouseDragged:dragging==1?kCGEventRightMouseDragged:kCGEventOtherMouseDragged;
        event=CGEventCreateMouseEvent(NULL,type,location,dragging<0?0:dragging);
    } else if (action==4) event=CGEventCreateScrollWheelEvent(NULL,kCGScrollEventUnitPixel,2,(int)y,(int)x);
    else {
        CGEventType type=button==0?(action==1?kCGEventLeftMouseDown:kCGEventLeftMouseUp):button==1?(action==1?kCGEventRightMouseDown:kCGEventRightMouseUp):(action==1?kCGEventOtherMouseDown:kCGEventOtherMouseUp);
        event=CGEventCreateMouseEvent(NULL,type,location,button);
        if (event) CGEventSetIntegerValueField(event,kCGMouseEventClickState,1);
    }
    if (event) { CGEventSetFlags(event,flags); CGEventPost(kCGHIDEventTap,event); CFRelease(event); }
}

// Modern aerial wallpapers are not reflected by desktopImageURLForScreen.
// Resolve only the selected asset; never pick an unrelated cached wallpaper.
static NSString *URModernWallpaper(NSScreen *screen, BOOL *handled) {
    NSString *root=[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/com.apple.wallpaper"];
    NSDictionary *store=[NSDictionary dictionaryWithContentsOfFile:[root stringByAppendingPathComponent:@"Store/Index.plist"]];
    if (![store isKindOfClass:NSDictionary.class]) return nil;
    NSDictionary *selection=nil;
    NSNumber *display=screen.deviceDescription[@"NSScreenNumber"];
    if (display) {
        CFUUIDRef uuid=CGDisplayCreateUUIDFromDisplayID(display.unsignedIntValue);
        if (uuid) {
            NSString *identifier=CFBridgingRelease(CFUUIDCreateString(NULL,uuid)); CFRelease(uuid);
            NSDictionary *displays=store[@"Displays"];
            if ([displays isKindOfClass:NSDictionary.class]) selection=displays[identifier];
        }
    }
    if (![selection isKindOfClass:NSDictionary.class]) selection=store[@"AllSpacesAndDisplays"];
    if (![selection isKindOfClass:NSDictionary.class]) return nil;
    NSDictionary *desktop=selection[@"Desktop"] ?: selection[@"Linked"];
    if (![desktop isKindOfClass:NSDictionary.class]) return nil;
    NSDictionary *content=desktop[@"Content"];
    NSArray *choices=[content isKindOfClass:NSDictionary.class] ? content[@"Choices"] : nil;
    if (![choices isKindOfClass:NSArray.class] || choices.count!=1) return nil;
    NSDictionary *choice=choices.firstObject;
    if (![choice isKindOfClass:NSDictionary.class] || ![choice[@"Provider"] isEqual:@"com.apple.wallpaper.choice.aerials"]) return nil;
    *handled=YES;
    id configuration=choice[@"Configuration"];
    if ([configuration isKindOfClass:NSData.class])
        configuration=[NSPropertyListSerialization propertyListWithData:configuration options:NSPropertyListImmutable format:NULL error:NULL];
    NSString *asset=[configuration isKindOfClass:NSDictionary.class] ? configuration[@"assetID"] : nil;
    if (![asset isKindOfClass:NSString.class] || ![[NSUUID alloc] initWithUUIDString:asset]) return nil;
    NSString *thumbnail=[root stringByAppendingPathComponent:[NSString stringWithFormat:@"aerials/thumbnails/%@.png",asset]];
    return [[NSFileManager defaultManager] isReadableFileAtPath:thumbnail] ? thumbnail : nil;
}

// Read the configured wallpaper only; never substitute a desktop screenshot.
int ur_wallpaper_path(char *buffer, int capacity) {
    @autoreleasepool {
        __block NSString *path=nil;
        void (^read)(void)=^{
            NSScreen *screen=NSScreen.screens.firstObject;
            if (screen) {
                BOOL handled=NO;
                path=URModernWallpaper(screen,&handled);
                if (!handled) path=[[NSWorkspace sharedWorkspace] desktopImageURLForScreen:screen].path;
            }
        };
        if (NSThread.isMainThread) read(); else dispatch_sync(dispatch_get_main_queue(),read);
        NSData *data=[path dataUsingEncoding:NSUTF8StringEncoding];
        if (!data.length || data.length>=32768) return 0;
        if (buffer && capacity>(int)data.length) { memcpy(buffer,data.bytes,data.length); buffer[data.length]=0; }
        return (int)data.length+1;
    }
}
int ur_wallpaper_render(const char *source, const char *destination) {
    @autoreleasepool {
        NSImage *image=[[NSImage alloc] initWithContentsOfFile:[NSString stringWithUTF8String:source]];
        if (!image || image.size.width<=0 || image.size.height<=0) return 0;
        NSBitmapImageRep *bitmap=[[NSBitmapImageRep alloc] initWithBitmapDataPlanes:NULL pixelsWide:960 pixelsHigh:540 bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSDeviceRGBColorSpace bytesPerRow:0 bitsPerPixel:0];
        if (!bitmap) return 0;
        [NSGraphicsContext saveGraphicsState];
        [NSGraphicsContext setCurrentContext:[NSGraphicsContext graphicsContextWithBitmapImageRep:bitmap]];
        double scale=MAX(960.0/image.size.width,540.0/image.size.height);
        NSRect target=NSMakeRect((960-image.size.width*scale)/2,(540-image.size.height*scale)/2,image.size.width*scale,image.size.height*scale);
        [image drawInRect:target fromRect:NSZeroRect operation:NSCompositingOperationCopy fraction:1];
        [NSGraphicsContext restoreGraphicsState];
        NSData *png=[bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
        return png.length>0 && png.length<=5000000 && [png writeToFile:[NSString stringWithUTF8String:destination] atomically:YES] ? 1 : 0;
    }
}

