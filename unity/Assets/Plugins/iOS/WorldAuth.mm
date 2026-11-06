// In-app browser sheet for World ID approvals (ASWebAuthenticationSession). The sheet closes itself when the
// backend redirects to ironcorner://; the game learns the result by polling GET /approvals/:id, so no callback is needed.
#import <AuthenticationServices/AuthenticationServices.h>
#import <UIKit/UIKit.h>

@interface IcWorldAuth : NSObject <ASWebAuthenticationPresentationContextProviding>
@property (nonatomic, strong) ASWebAuthenticationSession *session;
@end

@implementation IcWorldAuth
- (ASPresentationAnchor)presentationAnchorForWebAuthenticationSession:(ASWebAuthenticationSession *)session {
    for (UIScene *s in UIApplication.sharedApplication.connectedScenes)
        if ([s isKindOfClass:UIWindowScene.class])
            for (UIWindow *w in ((UIWindowScene *)s).windows) if (w.isKeyWindow) return w;
    return UIApplication.sharedApplication.windows.firstObject;
}
@end

static IcWorldAuth *ic;

extern "C" void IcWorldAuthOpen(const char *url) {
    NSURL *u = [NSURL URLWithString:[NSString stringWithUTF8String:url]];
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!ic) ic = [IcWorldAuth new];
        [ic.session cancel];
        ic.session = [[ASWebAuthenticationSession alloc] initWithURL:u callbackURLScheme:@"ironcorner"
            completionHandler:^(NSURL *cb, NSError *err) { ic.session = nil; }];
        ic.session.presentationContextProvider = ic;
        ic.session.prefersEphemeralWebBrowserSession = YES; // fresh login every time, and no "wants to sign in" alert
        [ic.session start];
    });
}
