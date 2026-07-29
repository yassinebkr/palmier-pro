import AppKit
import CoreText
import PalmierPro

Log.bootstrap()
Telemetry.start()
Analytics.start()
Analytics.capture(.appOpened)
BundledFonts.register()

// Install the platform font-name → bold/italic inference used by
// PalmierCore.TextStyle's tolerant decode. Core has no font framework, so it
// defaults to no inference; the app supplies the AppKit-based lookup so legacy
// files keyed by PostScript name (e.g. "Helvetica-Bold") decode correctly.
TextStyle.usePlatformFontTraitInference { fontName, size in
    guard let font = NSFont(name: fontName, size: CGFloat(size)) else { return (false, false) }
    let traits = CTFontGetSymbolicTraits(font as CTFont)
    return (traits.contains(.traitBold), traits.contains(.traitItalic))
}

AccountService.shared.configure()
ModelCatalog.shared.configure()

// Shorten the default tooltip delay from 2s to 0.01s.
UserDefaults.standard.set(10, forKey: "NSInitialToolTipDelay")

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.mainMenu = MainMenuBuilder.buildMenu()
app.run()
