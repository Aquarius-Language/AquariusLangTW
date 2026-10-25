#import <Cocoa/Cocoa.h>
#import <QuartzCore/CAMetalLayer.h>

void* aqua_metal_layer(void* handle) {
    NSWindow* window = (NSWindow*)handle;
    NSView* view = [window contentView];
    [view setWantsLayer:YES];
    CAMetalLayer* layer = [CAMetalLayer layer];
    layer.contentsScale = [window backingScaleFactor];
    [view setLayer:layer];
    return layer;
}
