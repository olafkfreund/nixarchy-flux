import FluxKit
import SwiftUI

/// The screen of a route on the navigation stack of a tab. Each screen
/// takes the theme surfaces once at its root, see `View.fluxScreen()`.
struct RouteDestination: View {
    let route: Route

    var body: some View {
        switch route {
        case .settings: SettingsView().fluxScreen()
        case .computer(let id): ComputerPage(deviceId: id).fluxScreen()
        case .feature(let r): FeatureDestination(route: r).fluxScreen()
        }
    }
}

/// The tab bar of the theme. It goes on the navigation stack of a tab, so
/// that the screens that the stack pushes keep it.
struct ThemedTabBar: ViewModifier {
    @Environment(\.tn) private var tn

    func body(content: Content) -> some View {
        content
            .toolbarBackground(tn.tile, for: .tabBar)
            .toolbarBackground(.visible, for: .tabBar)
            .toolbarColorScheme(tn.dark ? .dark : .light, for: .tabBar)
    }
}

extension View {
    /// The screens of the routes, see `RouteDestination`.
    func routeDestinations() -> some View {
        navigationDestination(for: Route.self) { RouteDestination(route: $0) }
    }
}
