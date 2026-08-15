using System.IO;
using System.Text.RegularExpressions;
using TrimKit.Models;

namespace TrimKit.Services;

/// <summary>
/// Resolves NTLite/WinReducer component IDs to actual removal actions against a mounted image.
/// Handles 1000+ components by category prefix pattern matching:
/// - driver_xxxxx.inf → DriverStore\FileRepository removal
/// - kl-XXXXXXXX → keyboard layout DLL removal  
/// - font_* → font file removal
/// - lang* → language/MUI removal
/// - hwsupport_* → capability or file removal
/// - microsoft.* → provisioned app removal
/// - Named system components → file/registry/service removal
/// </summary>
public static partial class NtLiteComponentMap
{
    /// <summary>
    /// Resolves all removal actions from a preset's RemoveList against the mounted image.
    /// Handles all NTLite component ID formats at scale (1000+ items).
    /// </summary>
    public static ResolvedRemovalPlan ResolvePreset(List<PresetComponent> removeList, string mountPath)
    {
        var plan = new ResolvedRemovalPlan();
        var driverStore = Path.Combine(mountPath, @"Windows\System32\DriverStore\FileRepository");
        var fontsDir = Path.Combine(mountPath, @"Windows\Fonts");
        var system32 = Path.Combine(mountPath, @"Windows\System32");

        foreach (var item in removeList)
        {
            var id = item.Id.Trim().ToLowerInvariant();

            // === DRIVERS: driver_xxxxx.inf → remove DriverStore\FileRepository\xxxxx* ===
            if (id.StartsWith("driver_"))
            {
                var infName = id["driver_".Length..]; // e.g. "acpi.inf"
                var baseName = Path.GetFileNameWithoutExtension(infName); // e.g. "acpi"

                if (Directory.Exists(driverStore))
                {
                    foreach (var dir in Directory.GetDirectories(driverStore, $"{baseName}*"))
                    {
                        plan.DirectoriesToDelete.Add(dir);
                    }
                }
                continue;
            }

            // === KEYBOARD LAYOUTS: kl-XXXXXXXX → remove kbd DLL by layout ID ===
            if (id.StartsWith("kl-"))
            {
                var layoutId = id["kl-".Length..]; // e.g. "00000409"
                // Windows keyboard layouts map to kbdXXX.dll files
                // The mapping is in HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layouts\XXXXXXXX
                // But we can just try to find matching kbd*.dll files
                var kbdDll = GetKeyboardDllForLayout(layoutId);
                if (kbdDll != null)
                {
                    var path32 = Path.Combine(system32, kbdDll);
                    var pathWow = Path.Combine(mountPath, @"Windows\SysWOW64", kbdDll);
                    if (File.Exists(path32)) plan.FilesToDelete.Add(path32);
                    if (File.Exists(pathWow)) plan.FilesToDelete.Add(pathWow);
                }
                continue;
            }

            // === FONTS: font_* → remove from Windows\Fonts ===
            if (id.StartsWith("font_"))
            {
                var fontPatterns = GetFontFilePatterns(id);
                if (Directory.Exists(fontsDir))
                {
                    foreach (var pattern in fontPatterns)
                    {
                        try
                        {
                            foreach (var f in Directory.GetFiles(fontsDir, pattern))
                                plan.FilesToDelete.Add(f);
                        }
                        catch { /* Directory may not exist or be inaccessible */ }
                    }
                }
                continue;
            }

            // === LANGUAGES: lang* → remove MUI directory and capabilities ===
            if (id.StartsWith("lang"))
            {
                var langTag = GetLanguageTag(id);
                if (langTag != null)
                {
                    plan.LanguagesToRemove.Add(langTag);
                    // Also remove the MUI directory if it exists
                    var muiDir = Path.Combine(system32, langTag);
                    if (Directory.Exists(muiDir))
                        plan.DirectoriesToDelete.Add(muiDir);
                }
                continue;
            }

            // === HARDWARE SUPPORT: hwsupport_* → capabilities ===
            if (id.StartsWith("hwsupport_"))
            {
                var capName = id["hwsupport_".Length..];
                plan.CapabilitiesToRemove.Add(capName);
                continue;
            }

            // === MICROSOFT APPS: microsoft.* → provisioned app ===
            if (id.StartsWith("microsoft."))
            {
                plan.AppsToRemove.Add(id);
                continue;
            }

            // === NAMED SYSTEM COMPONENTS → specific file/service actions ===
            var action = GetNamedComponentAction(id, mountPath);
            if (action != null)
            {
                plan.FilesToDelete.AddRange(action.Files);
                plan.DirectoriesToDelete.AddRange(action.Directories);
                if (action.ServiceToDisable != null)
                    plan.ServicesToDisable.Add(action.ServiceToDisable);
            }
        }

        return plan;
    }

    #region Keyboard Layout Mapping

    /// <summary>
    /// Maps common keyboard layout IDs to their DLL filenames.
    /// Layout ID is from HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layouts
    /// </summary>
    private static string? GetKeyboardDllForLayout(string layoutId)
    {
        // Most common mappings — Windows uses kbdXX.dll naming
        return layoutId switch
        {
            "00000401" => "kbda1.dll",    // Arabic
            "00000402" => "kbdbu.dll",    // Bulgarian
            "00000404" => "kbdus.dll",    // Chinese (Traditional) — uses US layout
            "00000405" => "kbdcz.dll",    // Czech
            "00000406" => "kbdda.dll",    // Danish
            "00000407" => "kbdgr.dll",    // German
            "00000408" => "kbdhe.dll",    // Greek
            "00000409" => null,           // US English — never remove
            "0000040a" => "kbdsp.dll",    // Spanish
            "0000040b" => "kbdfi.dll",    // Finnish
            "0000040c" => "kbdfr.dll",    // French
            "0000040d" => "kbdheb.dll",   // Hebrew
            "0000040e" => "kbdhu.dll",    // Hungarian
            "0000040f" => "kbdic.dll",    // Icelandic
            "00000410" => "kbdit.dll",    // Italian
            "00000411" => "kbdjpn.dll",   // Japanese
            "00000412" => "kbdkor.dll",   // Korean
            "00000413" => "kbdne.dll",    // Dutch
            "00000414" => "kbdno.dll",    // Norwegian
            "00000415" => "kbdpl1.dll",   // Polish
            "00000416" => "kbdbr.dll",    // Portuguese (Brazil)
            "00000418" => "kbdro.dll",    // Romanian
            "00000419" => "kbdru.dll",    // Russian
            "0000041a" => "kbdcr.dll",    // Croatian
            "0000041b" => "kbdsl.dll",    // Slovak
            "0000041c" => "kbdal.dll",    // Albanian
            "0000041d" => "kbdsw.dll",    // Swedish
            "0000041e" => "kbdth0.dll",   // Thai
            "0000041f" => "kbdtuq.dll",   // Turkish
            "00000420" => "kbdurdu.dll",  // Urdu
            "00000422" => "kbdur.dll",    // Ukrainian
            "00000424" => "kbdcr.dll",    // Slovenian
            "00000425" => "kbdest.dll",   // Estonian
            "00000426" => "kbdlv.dll",    // Latvian
            "00000427" => "kbdlt.dll",    // Lithuanian
            "00000429" => "kbdfa.dll",    // Persian
            "0000042a" => "kbdvntc.dll",  // Vietnamese
            _ => $"kbd{layoutId[6..]}.dll" // Generic fallback
        };
    }

    #endregion

    #region Font Mapping

    private static string[] GetFontFilePatterns(string id)
    {
        // NTLite font IDs map to font filenames
        return id switch
        {
            "font_arialblack" => ["ariblk.ttf"],
            "font_bahnschrift" => ["bahnschrift.ttf"],
            "font_calibri" => ["calibri*.ttf"],
            "font_cambria" => ["cambria*.tt*"],
            "font_cambria_regular" => ["cambria.ttc"],
            "font_candara" => ["Candara*.ttf"],
            "font_comicsansms" => ["comic*.ttf"],
            "font_constantia" => ["constan*.ttf"],
            "font_corbel" => ["corbel*.ttf"],
            "font_courier" => ["cour*.ttf"],
            "font_ebrima" => ["ebrima*.ttf"],
            "font_franklingothic" => ["framd*.ttf"],
            "font_gabriola" => ["gabriola.ttf"],
            "font_georgia" => ["georgia*.ttf"],
            "font_impact" => ["impact.ttf"],
            "font_inkfree" => ["Inkfree.ttf"],
            "font_javanesetext" => ["javatext.ttf"],
            "font_lucidasans" => ["l_10646.ttf"],
            "font_malgungothic" => ["malgun*.ttf"],
            "font_microsofthimalaya" => ["himalaya.ttf"],
            "font_msgothic" => ["msgoth*.ttc"],
            "font_mvboli" => ["mvboli.ttf"],
            "font_myanmartext" => ["mmrtext*.ttf"],
            "font_palatinolinotype" => ["pala*.ttf"],
            "font_segoe_ui_variable" => ["SegUIVar.ttf"],
            "font_segoeprint" => ["segoepr*.ttf"],
            "font_segoescript" => ["segoesc*.ttf"],
            "font_simsun" => ["simsun*.ttc"],
            "font_sitka" => ["sitk*.ttf"],
            "font_sylfaen" => ["sylfaen.ttf"],
            "font_trebuchetms" => ["trebuc*.ttf"],
            "font_verdana" => ["verdana*.ttf"],
            "font_webdings" => ["webdings.ttf"],
            "font_yugothic" => ["YuGoth*.ttc"],
            _ => [$"{id.Replace("font_", "")}*.*"]
        };
    }

    #endregion

    #region Language Mapping

    private static string? GetLanguageTag(string id)
    {
        return id switch
        {
            "langafrikaans" => "af-ZA", "langalbanian" => "sq-AL", "langarabic" => "ar-SA",
            "langarmenian" => "hy-AM", "langassamese" => "as-IN", "langazerbaijani" => "az-Latn-AZ",
            "langbasque" => "eu-ES", "langbelarusian" => "be-BY", "langbengali_india" => "bn-IN",
            "langbosnian" => "bs-Latn-BA", "langbulgarian" => "bg-BG", "langcatalan" => "ca-ES",
            "langcherokee" => "chr-Cher-US", "langchineses" => "zh-CN", "langcroatian" => "hr-HR",
            "langczech" => "cs-CZ", "langdanish" => "da-DK", "langdutch" => "nl-NL",
            "langenglishgb" => "en-GB", "langestonian" => "et-EE", "langfilipino" => "fil-PH",
            "langfinnish" => "fi-FI", "langfrench" => "fr-FR", "langfrenchcanadian" => "fr-CA",
            "langgalician" => "gl-ES", "langgeorgian" => "ka-GE", "langgerman" => "de-DE",
            "langgreek" => "el-GR", "langgujarati" => "gu-IN", "langhebrew" => "he-IL",
            "langhindi" => "hi-IN", "langhungarian" => "hu-HU", "langicelandic" => "is-IS",
            "langindonesian" => "id-ID", "langirish" => "ga-IE", "langitalian" => "it-IT",
            "langkannada" => "kn-IN", "langkazakh" => "kk-KZ", "langkhmer" => "km-KH",
            "langkonkani" => "kok-IN", "langkorean" => "ko-KR", "langlao" => "lo-LA",
            "langlatvian" => "lv-LV", "langlithuanian" => "lt-LT", "langluxembourgish" => "lb-LU",
            "langmacedonian" => "mk-MK", "langmalay_malaysia" => "ms-MY", "langmalayalam" => "ml-IN",
            "langmaltese" => "mt-MT", "langmaori" => "mi-NZ", "langmarathi" => "mr-IN",
            "langnepali" => "ne-NP", "langnorwegian" => "nb-NO", "langodia" => "or-IN",
            "langpersian" => "fa-IR", "langpolish" => "pl-PL", "langportuguesebr" => "pt-BR",
            "langportuguesept" => "pt-PT", "langpunjabi" => "pa-IN", "langquechua" => "quz-PE",
            "langromanian" => "ro-RO", "langrussian" => "ru-RU", "langscottish" => "gd-GB",
            "langserbian" => "sr-Latn-RS", "langslovak" => "sk-SK", "langslovenian" => "sl-SI",
            "langspanish" => "es-ES", "langswedish" => "sv-SE", "langtamil" => "ta-IN",
            "langtatar" => "tt-RU", "langtelugu" => "te-IN", "langthai" => "th-TH",
            "langturkish" => "tr-TR", "langukrainian" => "uk-UA", "langurdu" => "ur-PK",
            "languyghur" => "ug-CN", "languzbek" => "uz-Latn-UZ", "langvalencian" => "ca-ES-valencia",
            "langvietnamese" => "vi-VN", "langwelsh" => "cy-GB", "langamharic" => "am-ET",
            _ => null
        };
    }

    #endregion

    #region Named System Components

    private static NamedComponentAction? GetNamedComponentAction(string id, string mountPath)
    {
        var sys32 = Path.Combine(mountPath, @"Windows\System32");

        return id switch
        {
            // Services to disable
            "windowsupdate" => new NamedComponentAction { ServiceToDisable = "wuauserv" },
            "smbv1" => new NamedComponentAction { ServiceToDisable = "mrxsmb10" },
            "smbdirect" => new NamedComponentAction { ServiceToDisable = "SmbDirect" },
            "webthreatdefense" => new NamedComponentAction { ServiceToDisable = "webthreatdefsvc" },
            "wiredautoconfig" => new NamedComponentAction { ServiceToDisable = "dot3svc" },
            "wmisvc" => new NamedComponentAction { ServiceToDisable = "Winmgmt" },
            "wpdbusenum" => new NamedComponentAction { ServiceToDisable = "WPDBusEnum" },
            "wwanautoconfig" => new NamedComponentAction { ServiceToDisable = "WwanSvc" },

            // File/directory deletions
            "soundsdefault" or "soundthemes" => new NamedComponentAction
                { Directories = [Path.Combine(mountPath, @"Windows\Media")] },
            "retaildemo" => new NamedComponentAction
                { Directories = [Path.Combine(sys32, "RetailDemo")] },
            "migwiz" => new NamedComponentAction
                { Directories = [Path.Combine(sys32, "migwiz")] },
            "winre" or "winrewim" => new NamedComponentAction
                { Files = [Path.Combine(mountPath, @"Windows\System32\Recovery\Winre.wim")] },
            "winsat" => new NamedComponentAction
                { Files = [Path.Combine(sys32, "WinSAT.exe"), Path.Combine(sys32, "WinSATAPI.dll")] },
            "dvdplay" => new NamedComponentAction
                { Files = [Path.Combine(sys32, "dvdplay.exe")] },
            "xbox" => new NamedComponentAction
                { Directories = [Path.Combine(mountPath, @"Windows\SystemApps\Microsoft.Xbox*")] },
            "pos" => new NamedComponentAction
                { Directories = [Path.Combine(sys32, "PointOfService")] },
            "zipfolder" => new NamedComponentAction
                { Files = [Path.Combine(sys32, "zipfldr.dll")] },
            "winsxs" => null, // Never auto-delete WinSxS — handled separately
            "winsetup" => null, // Never remove setup
            "wlan" => null, // Protected by SafetyGuard

            _ => null // Unmapped — skip silently
        };
    }

    #endregion
}

public class NamedComponentAction
{
    public List<string> Files { get; set; } = [];
    public List<string> Directories { get; set; } = [];
    public string? ServiceToDisable { get; set; }
}

public class ResolvedRemovalPlan
{
    public List<string> FilesToDelete { get; } = [];
    public List<string> DirectoriesToDelete { get; } = [];
    public List<string> ServicesToDisable { get; } = [];
    public List<string> CapabilitiesToRemove { get; } = [];
    public List<string> LanguagesToRemove { get; } = [];
    public List<string> DriversToRemove { get; } = [];
    public List<string> AppsToRemove { get; } = [];

    public int TotalActions => FilesToDelete.Count + DirectoriesToDelete.Count +
                               ServicesToDisable.Count + CapabilitiesToRemove.Count +
                               LanguagesToRemove.Count + DriversToRemove.Count +
                               AppsToRemove.Count;
}
