using System.Globalization;
using System.Reflection;

namespace WinApkLauncher;

/// <summary>A UI language: its code in profiles.json, its own name and its writing direction.</summary>
internal sealed record UiLanguage(string Code, string NativeName, bool Rtl);

/// <summary>
/// Every string the launcher's windows show, in each UI language.
///
/// Each entry passes its values to <see cref="T"/> in the order of <see cref="Languages"/>: English
/// first, then Hebrew. To add a language, add it to <see cref="Languages"/>, then add a value at its
/// position to the entries; an entry without one shows English. <c>--self-test</c> lists the
/// properties that have no translation.
///
/// Console output (<c>--help</c>, <c>--list</c>, <c>--dry-run</c> ...) and the logs stay English:
/// only the GUI calls <see cref="Apply"/>, so code shared with the console gets English there.
///
/// In a right-to-left language, wrap paths, file names, versions and other Latin text in
/// <see cref="Ltr"/>, and names the user typed in <see cref="Iso"/>: each becomes one unit, so
/// hyphens, slashes, dots and parentheses at its ends keep their places.
/// </summary>
internal static class L
{
    public static readonly UiLanguage[] Languages =
    {
        new("en", "English", Rtl: false),
        new("he", "עברית", Rtl: true),
    };

    /// <summary>The Language setting that follows the Windows display language.</summary>
    public const string Auto = "auto";

    private static int _current; // index into Languages

    public static UiLanguage Current => Languages[_current];
    public static bool Rtl => Current.Rtl;

    /// <summary>"auto" or a code from <see cref="Languages"/>; anything else is "auto".</summary>
    public static string NormalizeSetting(string? setting)
    {
        var s = setting?.Trim().ToLowerInvariant();
        return Languages.Any(l => l.Code == s) ? s! : Auto;
    }

    /// <summary>The language a setting stands for. "auto": Hebrew when Windows shows Hebrew (he, or the old iw), else English.</summary>
    public static string Resolve(string? setting)
    {
        var s = NormalizeSetting(setting);
        if (s != Auto) return s;
        for (var c = CultureInfo.CurrentUICulture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            var code = c.TwoLetterISOLanguageName.ToLowerInvariant();
            if (code == "iw") code = "he";
            if (Languages.Any(l => l.Code == code)) return code;
        }
        return "en";
    }

    /// <summary>Use the language of this setting for the strings from now on.</summary>
    public static void Apply(string? setting)
    {
        var code = Resolve(setting);
        _current = Math.Max(0, Array.FindIndex(Languages, l => l.Code == code));
    }

    /// <summary>The value for the current language; English when the entry has none.</summary>
    private static string T(params string?[] values) =>
        _current < values.Length && !string.IsNullOrEmpty(values[_current]) ? values[_current]! : values[0]!;

    // ----- direction -----

    // The isolates (LRI, FSI, PDI) would fit best, but GDI text, which WinForms draws with, shows
    // them as boxes and does not isolate. An embedding (LRE or RLE … PDF) between two marks of the
    // surrounding direction (RLM or LRM) does the same: the text inside keeps its direction, and the
    // punctuation around it follows the sentence.
    private const char LRE = '\u202A', RLE = '\u202B', PDF = '\u202C', LRM = '\u200E', RLM = '\u200F';

    /// <summary>Left-to-right text (a path, a file name, a version, an env var, "-Xmx2g") as one unit inside right-to-left text.</summary>
    public static string Ltr(string? s) => !Rtl || string.IsNullOrEmpty(s) ? s ?? "" : RLM.ToString() + LRE + s + PDF + RLM;

    /// <summary>True when the first letter of <paramref name="s"/> is Hebrew or Arabic.</summary>
    public static bool StartsRtl(string? s)
    {
        foreach (var c in s ?? "")
        {
            if (IsRtlLetter(c)) return true;
            if (char.IsLetter(c)) return false;
        }
        return false;
    }

    /// <summary>
    /// A name the user typed, on one line, as one unit in its own direction (that of its first
    /// letter, like FSI) inside the sentence: "PF Notes (דרך SOCKS5)" stays whole in Hebrew text, a
    /// Hebrew name stays whole in English text. For text that wraps, set the paragraph direction instead.
    /// </summary>
    public static string Iso(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool rtlName = StartsRtl(s);
        if (!Rtl && !rtlName) return s;
        var mark = Rtl ? RLM : LRM;
        return mark.ToString() + (rtlName ? RLE : LRE) + s + PDF + mark;
    }

    private static bool IsRtlLetter(char c) => c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF';

    /// <summary>A name in quotes: ‘name’ in English, "name" in Hebrew.</summary>
    private static string Q(string name) => T($"‘{Iso(name)}’", $"\"{Iso(name)}\"");

    /// <summary>Applies the language's direction to a window: mirrored layout for right-to-left.</summary>
    public static void ApplyDirection(Form f)
    {
        f.RightToLeft = Rtl ? RightToLeft.Yes : RightToLeft.No;
        f.RightToLeftLayout = Rtl;
    }

    /// <summary>A control that always reads left to right (paths, hosts, ports, JVM options, log text).</summary>
    public static TControl KeepLtr<TControl>(TControl c) where TControl : Control
    {
        c.RightToLeft = RightToLeft.No;
        return c;
    }

    // =====================================================================
    // Main window: toolbar and menus
    // =====================================================================

    public static string MainTitle => $"{AppInfo.ProductName} {AppInfo.Version}";
    public static string ToolbarName => T("Actions", "פעולות");
    public static string MoreOptions => T("More options", "אפשרויות נוספות");

    public static string Launch => T("Launch", "הפעל");
    public static string LaunchTip => T("Launch the selected app (Enter)", "הפעל את האפליקציה שנבחרה (Enter)");
    public static string AddApp => T("Add app", "הוסף אפליקציה");
    public static string AddAppTip => T("Add an app profile (Ctrl+N). You can also drop an .apk / .xapk on the window.",
        $"הוסף פרופיל של אפליקציה (Ctrl+N). אפשר גם לגרור קובץ {Ltr(".apk")} או {Ltr(".xapk")} אל החלון.");
    public static string Edit => T("Edit", "ערוך");
    public static string EditTip => T("Edit the selected profile (F2)", "ערוך את הפרופיל שנבחר (F2)");
    public static string Duplicate => T("Duplicate", "שכפל");
    public static string DuplicateTip => T("Duplicate the selected profile (Ctrl+D)", "שכפל את הפרופיל שנבחר (Ctrl+D)");
    public static string Delete => T("Delete", "מחק");
    public static string DeleteTip => T("Delete the selected profile (Del). The data root on disk is kept.",
        "מחק את הפרופיל שנבחר (Del). תיקיית הנתונים בדיסק נשמרת.");
    public static string RunLog => T("Run log", "יומן הרצה");
    public static string RunLogTip => T("Open the last run log (Ctrl+L)", "פתח את יומן ההרצה האחרון (Ctrl+L)");
    public static string DataFolder => T("Data folder", "תיקיית נתונים");
    public static string DataFolderTip => T("Open the selected profile's data folder", "פתח את תיקיית הנתונים של הפרופיל שנבחר");
    public static string ProxyLog => T("Proxy log", "יומן פרוקסי");
    public static string ProxyLogTip => T("Open the per-app network proxy log", "פתח את יומן פרוקסי הרשת של האפליקציה");
    public static string ViewList => T("List", "רשימה");
    public static string ViewListTip => T("List view (Ctrl+2)", "תצוגת רשימה (Ctrl+2)");
    public static string ViewTiles => T("Tiles", "אריחים");
    public static string ViewTilesTip => T("Tiles view (Ctrl+1)", "תצוגת אריחים (Ctrl+1)");
    public static string RuntimePath => T("Runtime path…", "נתיב ה-runtime…");
    public static string OpenLogsFolder => T("Open logs folder", "פתח את תיקיית היומנים");
    public static string OpenLogsFolderTip(string dir) => T("Open the folder with the run logs:\n" + dir,
        "פתח את התיקייה של יומני ההרצה:\n" + Ltr(dir));
    public static string About => T($"About {AppInfo.ProductName}", $"אודות {AppInfo.ProductName}");

    /// <summary>The language menu keeps both names, so it can be found in either language.</summary>
    public static string LanguageMenu => T("Language / שפה", "שפה / Language");
    public static string LanguageAuto => T("Automatic (Windows language)", "אוטומטי (שפת Windows)");

    public static string RuntimeTip(string? exe, RuntimeSource source) =>
        T("Locate winapk-runtime.exe", "אתר את winapk-runtime.exe") + "\n" + source switch
        {
            RuntimeSource.Settings => Ltr(exe) + T("  (chosen in settings)", "  (נבחר בהגדרות)"),
            RuntimeSource.Bundled => Ltr(exe) + T("  (bundled)", "  (מצורף)"),
            _ => T("Not found", "לא נמצא"),
        };

    public static string MenuLaunch => T("Launch", "הפעל");
    public static string MenuEdit => T("Edit…", "ערוך…");
    public static string MenuDuplicate => T("Duplicate", "שכפל");
    public static string MenuDelete => T("Delete", "מחק");
    public static string MenuOpenRunLog => T("Open run log", "פתח את יומן ההרצה");
    public static string MenuOpenProxyLog => T("Open proxy log", "פתח את יומן הפרוקסי");
    public static string MenuOpenDataFolder => T("Open data folder", "פתח את תיקיית הנתונים");
    public static string MenuCreateShortcut => T("Create desktop shortcut", "צור קיצור דרך בשולחן העבודה");
    public static string MenuAddApp => T("Add app…", "הוסף אפליקציה…");
    public static string MenuTilesView => T("Tiles view", "תצוגת אריחים");
    public static string MenuListView => T("List view", "תצוגת רשימה");

    // =====================================================================
    // Main window: tiles, list and status bar
    // =====================================================================

    public static string AppsHeader => T("Apps", "אפליקציות");
    public static string AppCount(int n) => n == 1 ? T("1 app", "אפליקציה אחת") : T($"{n} apps", $"{n} אפליקציות");
    public static string ProfileCount(int n) => n == 1 ? T("1 profile", "פרופיל אחד") : T($"{n} profiles", $"{n} פרופילים");
    public static string TileStarting => T("Starting…", "מתחיל…");
    public static string TileAdd => T("Add app", "הוסף אפליקציה");
    public static string TileAddTip => T("Add an app (or drop an .apk / .xapk here)",
        $"הוסף אפליקציה (או גרור לכאן קובץ {Ltr(".apk")} או {Ltr(".xapk")})");
    public static string EmptyTitle => T("No apps yet", "אין עדיין אפליקציות");
    public static string EmptyHint => T("Drag an .apk or .xapk here, or click Add app.",
        $"גרור לכאן קובץ {Ltr(".apk")} או {Ltr(".xapk")}, או לחץ על \"הוסף אפליקציה\".");

    public static string TipNoApk => T("(no APK)", "(אין APK)");
    public static string TipApiDefault => T("29 (default)", "29 (ברירת מחדל)");
    public static string TipProxy(string label) => T("Proxy: ", "פרוקסי: ") + Ltr(label);
    public static string TipRunning => T("Running", "פועל");

    public static string ColProfile => T("Profile", "פרופיל");
    public static string ColApk => "APK";
    public static string ColDataRoot => T("Data root", "תיקיית נתונים");
    public static string ColSize => T("Size (dp)", "גודל (dp)");
    public static string ColApi => "API";
    public static string ColMulti => T("Multi", "מרובה");
    public static string ColProxy => T("Proxy", "פרוקסי");
    public static string ColStatus => T("Status", "מצב");
    public static string ListYes => T("yes", "כן");
    public static string ListStarting => T("starting…", "מתחיל…");
    public static string ListRunning => T("running", "פועל");

    public static string SummaryStarting(int n) => T($"{n} starting", $"{n} בהפעלה");
    public static string SummaryRunning(int n) => T($"{n} running", n == 1 ? "1 פועלת" : $"{n} פועלות");
    public static string StatusNoRuntime => T("winapk-runtime.exe not found  •  use Runtime path… to locate it",
        $"{Ltr("winapk-runtime.exe")} לא נמצא  •  אתר אותו עם \"נתיב ה-runtime…\"");
    public static string StatusProfiles(int n, string storePath) => ProfileCount(n) + T("  •  settings: ", "  •  הגדרות: ") + Ltr(storePath);
    public static string StatusLastLog(string path) => T("last log: ", "יומן אחרון: ") + Ltr(path);
    public static string StatusRuntimeLogs(string path) => T("runtime data/logs: ", "יומני ה-runtime: ") + Ltr(path);
    public static string StatusRunLogs(string path) => T("run logs: ", "יומני הרצה: ") + Ltr(path);
    public static string StatusLaunched(string name, string log) =>
        T($"launched {Q(name)}  •  log: ", $"הופעלה {Q(name)}  •  יומן: ") + Ltr(log);
    public static string StatusStillStarting(string name) =>
        T($"{Q(name)} is still starting  •  not started again", $"{Q(name)} עדיין מתחילה  •  לא הופעלה שוב");
    public static string StatusBroughtToFront(string name) =>
        T($"{Q(name)} is already running  •  brought its window to the front", $"{Q(name)} כבר פועלת  •  החלון שלה הובא לחזית");
    public static string StatusRunning(string name, string log) =>
        T($"{Q(name)} is running  •  log: ", $"{Q(name)} פועלת  •  יומן: ") + Ltr(log);
    public static string StatusStopped(string name) => T($"stopped {Q(name)}", $"{Q(name)} נעצרה");
    public static string StatusStopFailed(int n, string name) =>
        T($"could not stop {n} process(es) of {Q(name)}", $"לא ניתן לעצור {Processes(n)} של {Q(name)}");
    public static string StatusShortcut(string path) => T("desktop shortcut created: ", "נוצר קיצור דרך בשולחן העבודה: ") + Ltr(path);

    private static string Processes(int n) => n == 1 ? "תהליך אחד" : $"{n} תהליכים";

    // =====================================================================
    // Startup: phases, the startup window and its stall hint
    // =====================================================================

    public static string PhaseStarting => T("Starting…", "מתחיל…");
    public static string PhaseInstalling => T("Installing the app…", "מתקין את האפליקציה…");
    public static string PhaseConverting => T("Converting the app's code (first launch only)…", "ממיר את קוד האפליקציה (רק בהפעלה הראשונה)…");
    public static string PhaseConvertingLong => T("Converting the app's code — first launch only, large apps take a few minutes…",
        "ממיר את קוד האפליקציה – רק בהפעלה הראשונה, אפליקציות גדולות לוקחות כמה דקות…");
    public static string PhaseStartingConverted(string summary) => T($"Starting… (code converted: {summary})", $"מתחיל… (הקוד הומר: {summary})");
    public static string CodeConverted(string summary) => T($" (code converted: {summary})", $" (הקוד הומר: {summary})");
    public static string ConvertedSummary(long classes, long ms) =>
        T($"{classes:N0} classes in {ms / 1000.0:0.0} s", $"{classes:N0} מחלקות ב-{ms / 1000.0:0.0} שניות");
    public static string StatusLine(string name, string phase, string elapsed) => $"{Q(name)}: {phase}  •  {elapsed}";

    public static string SplashTitle(string name) => T($"Starting {Iso(name)}", $"הפעלת {Iso(name)}");
    public static string SplashHide => T("Hide", "הסתר");
    public static string SplashHideTip => T("Close this window. The app keeps starting.", "סגור את החלון הזה. הפעלת האפליקציה תימשך.");
    public static string SplashOpenLog => T("Open log", "פתח יומן");
    public static string SplashOpenLogTip => T("Open the run log in Notepad", "פתח את יומן ההרצה בפנקס הרשימות");
    public static string SplashAlreadyRunning => T("Already running", "כבר פועלת");
    public static string SplashCouldNotStart => T("Couldn't start", "ההפעלה נכשלה");
    public static string SplashExited(string name, int code) =>
        T($"{Iso(name)} exited with code {code} before its window opened.", $"האפליקציה {Q(name)} נסגרה עם קוד {code} לפני שהחלון שלה נפתח.");
    public static string SplashLogHasDetails => T("The run log has the details.", "הפרטים נמצאים ביומן ההרצה.");
    public static string SplashFailedTitle(string name) => T($"{Iso(name)} couldn't start", $"הפעלת {Iso(name)} נכשלה");
    public static string SplashBarName => T("Starting", "מתחיל");
    public static string SplashDefaultName => T("App", "אפליקציה");
    public static string StallNoProgress(int minutes) =>
        T($"No progress for {minutes} min. The app may be stuck.", $"אין התקדמות כבר {minutes} דק׳ – ייתכן שהאפליקציה תקועה.");
    public static string StallStop => T("Stop", "עצור");
    public static string StallCpuBusy => T("Still working (CPU busy)…", "עדיין עובד (המעבד עסוק)…");

    // =====================================================================
    // Dialogs: already running (exit code 3), stop, crash, still starting
    // =====================================================================

    public static string AlreadyRunningTitle => T("Already running", "כבר פועלת");
    public static string AlreadyRunningHeading(string name) =>
        T($"{Iso(name)} is already running, or still starting in the background.",
          $"האפליקציה {Q(name)} כבר פועלת, או שהיא עדיין מתחילה ברקע.");
    public static string AlreadyRunningBody(string log) =>
        T("This launch did not start a second copy, so the app's data is safe. The first launch of a " +
          "large app can take several minutes before its window opens.\n\nLog: ",
          "ההפעלה הזו לא הפעילה עותק שני, ולכן הנתונים של האפליקציה בטוחים. בהפעלה הראשונה של " +
          "אפליקציה גדולה יכולות לעבור כמה דקות עד שהחלון שלה נפתח.\n\nיומן: ") + Ltr(log);
    public static string Wait => T("Wait", "המתן");
    public static string WaitTip => T("Close this message. The running copy carries on.", "סגור את ההודעה. העותק שפועל ממשיך לפעול.");
    public static string ShowIt => T("Show it", "הצג אותה");
    public static string ShowItTip => T("Bring its window to the front", "הבא את החלון שלה לחזית");
    public static string ShowItNoWindow => T("It has no window yet", "עדיין אין לה חלון");
    public static string StopIt => T("Stop it", "עצור אותה");
    public static string StopItTip => T("End the running copy", "סגור את העותק שפועל");
    public static string StopItNone => T("No runtime process for this app was found", "לא נמצא תהליך runtime של האפליקציה");

    public static string StopTitle => T("Stop app", "עצירת אפליקציה");
    public static string StopHeading(string name) => T($"Stop {Iso(name)}?", $"לעצור את {Q(name)}?");
    public static string StopBody(string name, int processes) =>
        T($"This ends {Iso(name)} at once" + (processes > 1 ? $" ({processes} processes)" : "") + ", " +
          "even if it is still preparing its first launch. Anything the app has not saved yet may be lost, " +
          "such as a message you are typing or data it has not written yet.",
          $"הפעולה סוגרת את {Q(name)} מיד" + (processes > 1 ? $" ({Processes(processes)})" : "") + ", " +
          "גם אם היא עדיין מתכוננת להפעלה הראשונה. מה שהאפליקציה עוד לא שמרה עלול ללכת לאיבוד, " +
          "למשל הודעה שאתה מקליד או נתונים שהיא עוד לא כתבה.");
    public static string StopConfirm => T("Stop it", "עצור");
    public static string Cancel => T("Cancel", "ביטול");
    public static string Ok => T("OK", "אישור");
    public static string Close => T("Close", "סגור");
    public static string NoRunningCopy(string name) =>
        T($"No running copy of {Iso(name)} was found. It may have exited already.", $"לא נמצא עותק פועל של {Q(name)}. ייתכן שהיא כבר נסגרה.");
    public static string CouldNotStop(int n, string name) =>
        T($"{n} process(es) of {Iso(name)} could not be stopped.", $"לא ניתן היה לעצור {Processes(n)} של {Q(name)}.");

    public static string CrashTitle => T("Launch crashed", "ההפעלה קרסה");
    public static string CrashHeading(string name, int code) =>
        T($"{Q(name)} exited with code {code} (crashed launch).", $"האפליקציה {Q(name)} נסגרה עם קוד {code} (ההפעלה קרסה).");
    public static string LogLine(string path) => T("Log: ", "יומן: ") + Ltr(path);
    public static string RuntimeLogsLine(string path) => T("Runtime data/logs: ", "יומני ה-runtime: ") + Ltr(path);
    public static string OpenLog => T("Open log", "פתח יומן");
    public static string OpenLogsFolderTipSelected => T("Open the logs folder in Explorer, with this log selected",
        "פתח את תיקיית היומנים בסייר הקבצים, כשהיומן הזה מסומן");

    public static string PathsNoticeHeading(string name, string root) =>
        T($"{Iso(name)} runs from {Ltr(root)}.", $"האפליקציה {Q(name)} פועלת מתוך {Ltr(root)}.");
    public static string PathsNoticeBody => T(
        "Its folders have letters that Windows' \"Language for non-Unicode programs\" can't show, so the launcher uses plain-letter folders there. Nothing needs to be set.",
        "בשמות התיקיות שלה יש אותיות שההגדרה \"שפה לתוכניות שאינן Unicode\" של Windows לא יכולה להציג, ולכן המפעיל משתמש שם בתיקיות עם אותיות פשוטות. אין צורך להגדיר דבר.");

    public static string StillStartingTitle => T("Still starting", "עדיין מתחילה");
    public static string StillStartingHeading(string name) => T($"{Iso(name)} is still starting.", $"האפליקציה {Q(name)} עדיין מתחילה.");
    public static string StillStartingBody => T("The first launch prepares the app and can take several minutes for large apps.",
        "ההפעלה הראשונה מכינה את האפליקציה, ובאפליקציות גדולות היא יכולה להימשך כמה דקות.");

    // =====================================================================
    // Messages of the main window
    // =====================================================================

    public static string SaveFailed => T("Save failed", "השמירה נכשלה");
    public static string CouldNotSave(string path, string error) => T("Could not save ", "לא ניתן לשמור את ") + Ltr(path) + ":\n" + error;

    public static string RuntimeNotFoundTitle => T("Runtime not found", "ה-runtime לא נמצא");
    public static string RuntimeNotFoundHeading => T("winapk-runtime.exe was not found.", $"הקובץ {Ltr("winapk-runtime.exe")} לא נמצא.");
    public static string RuntimeNotFoundBody(string bundled) =>
        T($"{AppInfo.ProductName} looks for the runtime in its settings and next to the launcher:\n",
          $"{AppInfo.ProductName} מחפש את ה-runtime בהגדרות שלו ובתיקייה שלו:\n") + Ltr(bundled) +
        T("\n\nLocate winapk-runtime.exe now?", $"\n\nלאתר את {Ltr("winapk-runtime.exe")} עכשיו?");
    public static string Locate => T("Locate…", "אתר…");
    public static string CannotLaunch => T("Cannot launch", "לא ניתן להפעיל");
    public static string LaunchFailed => T("Launch failed", "ההפעלה נכשלה");

    public static string NewProfileName => T("New profile", "פרופיל חדש");
    public static string CopySuffix => T(" (copy)", " (עותק)");

    public static string DeleteTitle => T("Delete profile", "מחיקת פרופיל");
    public static string DeleteHeading(string name) => T($"Delete profile {Q(name)}?", $"למחוק את הפרופיל {Q(name)}?");
    public static string DeleteBody => T("The data root on disk is NOT deleted.", "תיקיית הנתונים בדיסק לא תימחק.");

    public static string LogCaption => T("Log", "יומן");
    public static string NoRunLog(string dir) => T("No run log yet for this profile.\n\nRun logs are kept in:\n",
        "אין עדיין יומן הרצה לפרופיל הזה.\n\nיומני ההרצה נשמרים בתיקייה:\n") + Ltr(dir);
    public static string OpenFolderCaption => T("Open folder", "פתיחת תיקייה");
    public static string ProxyLogCaption => T("Proxy log", "יומן פרוקסי");
    public static string NoProxyConfigured => T("This profile has no network proxy configured.", "לא הוגדר פרוקסי רשת לפרופיל הזה.");
    public static string NoProxyLog(string name, string path) =>
        T($"No proxy log yet.\nIt is written the first time {Q(name)} is launched with a proxy.\n\n",
          $"אין עדיין יומן פרוקסי.\nהוא נכתב בפעם הראשונה ש-{Q(name)} מופעלת עם פרוקסי.\n\n") + Ltr(path);

    public static string ShortcutCaption => T("Desktop shortcut", "קיצור דרך בשולחן העבודה");
    public static string ShortcutCreated(string path) => T("Shortcut created on the desktop:\n", "נוצר קיצור דרך בשולחן העבודה:\n") + Ltr(path);
    public static string ShortcutFailed(string error) => T("Could not create the shortcut:\n", "לא ניתן ליצור את קיצור הדרך:\n") + error;
    public static string ShortcutNoShell => T("WScript.Shell is unavailable.", "WScript.Shell אינו זמין.");
    public static string ShortcutDescription(string name) => T($"Launch {Iso(name)} ({AppInfo.ProductName})", $"הפעל את {Iso(name)} ({AppInfo.ProductName})");

    public static string LocateRuntimeTitle => T("Locate winapk-runtime.exe", "איתור winapk-runtime.exe");
    public static string RuntimeFilter => T("winapk-runtime.exe|winapk-runtime.exe|Executables|*.exe|All files|*.*",
        "winapk-runtime.exe|winapk-runtime.exe|קבצי הפעלה|*.exe|כל הקבצים|*.*");

    public static string AboutText(string runtime, IEnumerable<string> runtimeSource, string settings, string data, string mode)
    {
        var lines = new List<string>
        {
            Ltr($"{AppInfo.ProductName} {AppInfo.Version}"),
            T("A launcher and per-app settings manager for Win.apk, which runs Android apps on Windows.",
              "משגר ומנהל הגדרות לכל אפליקציה עבור Win.apk, שמריץ אפליקציות Android ב-Windows."),
            "",
            T("Runtime:  ", "ה-runtime: ") + runtime,
        };
        lines.AddRange(runtimeSource.Select(l => "  " + Ltr(l)));
        lines.Add(T("Settings: ", "הגדרות: ") + Ltr(settings));
        lines.Add(T("Data:     ", "נתונים: ") + Ltr(data));
        lines.Add(T("Mode:     ", "מצב: ") + mode);
        lines.Add("");
        lines.Add(T("License: GNU AGPL-3.0. Source: ", "רישיון: GNU AGPL-3.0. קוד מקור: ") + Ltr(AppInfo.ProjectUrl));
        lines.Add(T("Win.apk by israel2176, based on Mac.apk by kksimp.", "Win.apk מאת israel2176, מבוסס על Mac.apk מאת kksimp."));
        lines.Add(T("Not affiliated with Google, WhatsApp or Meta.", "אין קשר ל-Google, ל-WhatsApp או ל-Meta."));
        return string.Join("\n", lines);
    }

    // ----- shared with the console (English there) -----

    public static string ModePortable => T("portable mode", "מצב נייד");
    public static string ModeInstalled => T("installed mode", "מצב מותקן");
    public static string RuntimeFromSettings(string exe) => Ltr(exe) + T("  (from settings)", "  (מההגדרות)");
    public static string RuntimeBundled(string exe) => Ltr(exe) + T("  (bundled next to the launcher)", "  (מצורף ל-WinApk Launcher)");
    public static string RuntimeMissing(string setting, string bundled) =>
        T("NOT FOUND", "לא נמצא") +
        (setting.Length == 0 ? "" : T(" (settings: ", " (בהגדרות: ") + Ltr(setting) + T(", missing)", ", הקובץ חסר)")) +
        T("; looked for ", "; חיפשתי את ") + Ltr(bundled);

    public static string ValidateNoRuntime => T("winapk-runtime.exe was not found.\nUse \"Runtime path…\" to locate it.",
        $"הקובץ {Ltr("winapk-runtime.exe")} לא נמצא.\nאתר אותו עם \"נתיב ה-runtime…\".");
    public static string ValidateRuntimeMissing(string path) =>
        T("winapk-runtime.exe not found:\n", $"הקובץ {Ltr("winapk-runtime.exe")} לא נמצא:\n") + Ltr(path);
    public static string ValidateApkMissing(string path) => T("APK not found:\n", "קובץ ה-APK לא נמצא:\n") + Ltr(path);
    public static string ValidateNoDataRoot => T("This profile has no data root.", "לפרופיל הזה אין תיקיית נתונים.");
    public static string LaunchBlocked => T("Launching is disabled in this mode (--screenshot).", $"ההפעלה מושבתת במצב הזה ({Ltr("--screenshot")}).");
    public static string AdapterFailed(string name, string error) =>
        T($"Could not start the network proxy adapter for {Q(name)}.\n{error}\n\n" +
          "The app was NOT launched (it would otherwise run without the configured proxy).",
          $"לא ניתן להפעיל את מתאם פרוקסי הרשת של {Q(name)}.\n{error}\n\n" +
          "האפליקציה לא הופעלה (אחרת היא הייתה פועלת בלי הפרוקסי שהוגדר).");
    public static string AdapterNoPort => T("the proxy adapter did not report a port", "מתאם הפרוקסי לא דיווח על פורט");
    public static string RuntimeDidNotStart(string error) => T("winapk-runtime did not start: ", "winapk-runtime לא הופעל: ") + error;
    public static string CouldNotReadLog(string error) => T($"(could not read log: {error})", $"(לא ניתן לקרוא את היומן: {error})");

    // =====================================================================
    // Logs dialog
    // =====================================================================

    public static string LogsMenu => T("Logs…", "יומנים…");
    public static string LogsMenuTip => T("Log size limits, the logs folder's size, Delete old logs",
        "מגבלות הגודל של היומנים, גודל תיקיית היומנים ומחיקת יומנים ישנים");
    public static string LogsTitle => T("Logs", "יומנים");
    public static string LogsFolder => T("Logs folder", "תיקיית היומנים");
    public static string LogsSizeNow => T("Size now", "גודל כרגע");
    public static string LogsSizeValue(long bytes, int files) =>
        T($"{bytes / 1048576.0:0.0} MB in {files} {(files == 1 ? "file" : "files")}",
          $"{Ltr($"{bytes / 1048576.0:0.0} MB")} ב-{(files == 1 ? "קובץ אחד" : $"{files} קבצים")}");
    public static string LogsMaxRun => T("Largest run log (MB)", "גודל מרבי ליומן הרצה (MB)");
    public static string LogsKeep => T("Run logs kept per app", "יומני הרצה שנשמרים לכל אפליקציה");
    public static string LogsMaxFolder => T("Largest logs folder (MB)", "גודל מרבי לתיקיית היומנים (MB)");
    public static string LogsHint => T(
        "A run log over its limit keeps its first 256 KB and its newest part. Older run logs are deleted " +
        "when an app is launched, oldest first. The logs of apps that are running are always kept.",
        $"יומן הרצה שמגיע לגודל המרבי שומר את {Ltr("256 KB")} הראשונים ואת החלק החדש ביותר. יומני הרצה " +
        "ישנים נמחקים כשמפעילים אפליקציה, הישנים קודם. היומנים של אפליקציות שפועלות נשמרים תמיד.");
    public static string LogsDeleteOld => T("Delete old logs", "מחק יומנים ישנים");
    public static string LogsDeleteOldTip => T("Delete every run log except the newest of each app. The logs of running apps are kept.",
        "מחק את כל יומני ההרצה חוץ מהחדש ביותר של כל אפליקציה. היומנים של אפליקציות שפועלות נשמרים.");
    public static string LogsDeleted(int files, long bytes) =>
        T($"deleted {files} {(files == 1 ? "file" : "files")} ({bytes / 1048576.0:0.0} MB)",
          $"נמחקו {(files == 1 ? "קובץ אחד" : $"{files} קבצים")} ({Ltr($"{bytes / 1048576.0:0.0} MB")})");

    // =====================================================================
    // Edit dialog
    // =====================================================================

    public static string AddProfileTitle => T("Add profile", "הוספת פרופיל");
    public static string EditProfileTitle => T("Edit profile", "עריכת פרופיל");
    public static string FieldName => T("Name", "שם");
    public static string FieldApk => "APK";
    public static string FieldDataRoot => T("Data root", "תיקיית נתונים");
    public static string FieldPhoneSize => T("Phone size (dp)", "גודל הטלפון (dp)");
    public static string FieldApi => T("Android API", "רמת API של Android");
    public static string FieldDensity => T("Density (dpi)", "צפיפות (dpi)");
    public static string FieldJvm => T("Extra JVM opts", "אפשרויות JVM נוספות");
    public static string FieldLogLevel => T("Log level", "רמת רישום");
    public static string FieldMulti => T("Allow multiple instances (WINAPK_ALLOW_SECOND_INSTANCE)",
        $"אפשר כמה מופעים במקביל ({Ltr("WINAPK_ALLOW_SECOND_INSTANCE")})");
    public static string FieldClassLoad => T("Detailed class-loading log (without MACAPK_QUIET_CLASSLOAD)",
        $"יומן מפורט של טעינת מחלקות (בלי {Ltr("MACAPK_QUIET_CLASSLOAD")})");
    public static string Browse => T("Browse…", "עיון…");
    public static string HintDataRoot => T("Each profile needs its OWN data root. Two profiles on the same APK\n" +
                                           "with different roots = two independent instances/accounts.",
        "לכל פרופיל צריכה להיות תיקיית נתונים משלו. שני פרופילים של אותו APK\n" +
        "עם תיקיות שונות = שני מופעים או חשבונות עצמאיים.");
    public static string HintDensity => T("0 = auto  •  WINAPK_DENSITY (160/240/320…)",
        $"0 = אוטומטי  •  {Ltr("WINAPK_DENSITY (160/240/320…)")}");
    public static string HintJvm => T("space-separated • MACAPK_JVM_OPTS (e.g. -Xmx2g)",
        $"מופרדות ברווחים  •  {Ltr("MACAPK_JVM_OPTS")} (לדוגמה {Ltr("-Xmx2g")})");
    public static string SectionProxy => T("Network proxy", "פרוקסי רשת");
    public static string FieldProxyType => T("Proxy type", "סוג הפרוקסי");
    public static string FieldHost => T("Host", "כתובת");
    public static string FieldPort => T("Port", "יציאה");
    public static string FieldUser => T("Username", "שם משתמש");
    public static string FieldOptional => T("(optional)", "(אופציונלי)");
    public static string FieldPassword => T("Password", "סיסמה");
    public static string ProxyTest => T("Test", "בדוק");
    public static string HintProxy => T("Covers the app's Java network traffic and the runtime's push client.\n" +
                                        "Not covered: calls / UDP, sockets opened by the app's native code.",
        "מכסה את תעבורת הרשת של קוד ה-Java באפליקציה ואת לקוח ההתראות (push) של ה-runtime.\n" +
        "לא מכוסים: שיחות ו-UDP, וחיבורים שקוד ה-native של האפליקציה פותח.");
    public static string PresetDefault => T("360 x 800  (default)", $"{Ltr("360 x 800")}  (ברירת מחדל)");
    public static string PresetCustom => T("Custom…", "מותאם אישית…");
    public static string ApiDefault => T("Default (29)", "ברירת מחדל (29)");
    public static string LogDefault => T("Default (all)", "ברירת מחדל (הכול)");
    public static string LogVerbose => T("Verbose (2)", "מפורט (2)");
    public static string LogDebug => T("Debug (3)", "ניפוי באגים (3)");
    public static string LogInfo => T("Info (4)", "מידע (4)");
    public static string LogWarn => T("Warn (5)", "אזהרה (5)");
    public static string LogError => T("Error (6)", "שגיאה (6)");
    public static string ProxyNone => T("None (direct)", "ללא (חיבור ישיר)");
    public static string NoApkSelected => T("No APK selected", "לא נבחר APK");
    public static string FileNotFound => T("file not found", "הקובץ לא נמצא");
    public static string MissingName => T("Missing name", "חסר שם");
    public static string GiveName => T("Give the profile a name.", "תן שם לפרופיל.");
    public static string MissingDataRoot => T("Missing data root", "חסרה תיקיית נתונים");
    public static string GiveDataRoot => T("Give the profile a data root.", "בחר תיקיית נתונים לפרופיל.");
    public static string MissingProxyHost => T("Missing proxy host", "חסרה כתובת פרוקסי");
    public static string GiveProxyHost => T("Give the proxy a host, or set the proxy type to None.",
        "הזן כתובת לפרוקסי, או בחר בסוג הפרוקסי \"ללא\".");
    public static string ApkFilter => T("Android packages|*.apk;*.xapk;*.apkm;*.apks|All files|*.*",
        "חבילות Android|*.apk;*.xapk;*.apkm;*.apks|כל הקבצים|*.*");
    public static string ProxyChooseFirst => T("Choose a proxy type and host first.", "בחר קודם סוג פרוקסי וכתובת.");
    public static string ProxyTesting => T("Testing…", "בודק…");

    // ----- the proxy test (shared with the console and the adapter, English there) -----

    public static string ProxyOk(string kind, string host, int code, long ms) =>
        T($"OK — {kind} proxy reachable, {host} responded {code} in {ms} ms",
          $"תקין – הפרוקסי ({Ltr(kind)}) זמין, {Ltr(host)} השיב {code} תוך {Ltr($"{ms} ms")}");
    public static string ProxyBadAnswer(string host, int code, string reason) =>
        T($"Reached {host} through the proxy but it answered {code} {reason}",
          $"החיבור ל-{Ltr(host)} דרך הפרוקסי הצליח, אבל התשובה הייתה {Ltr($"{code} {reason}".Trim())}");
    public static string ProxyTimedOut => T("timed out", "תם הזמן הקצוב");
    public static string ProxyConnectTimeout => T("timed out connecting to the proxy", "תם הזמן הקצוב להתחברות לפרוקסי");
    public static string ProxyUnreachable(string hostPort, string error) =>
        T($"cannot reach proxy {hostPort} ({error})", $"אין גישה לפרוקסי {Ltr(hostPort)} ({Ltr(error)})");
    public static string ProxyNotConfigured => T("no proxy configured", "לא הוגדר פרוקסי");
    public static string ProxyNotSocks5 => T("not a SOCKS5 proxy", "זה לא פרוקסי SOCKS5");
    public static string ProxyRejectedAuthMethods => T("proxy rejected the offered auth methods", "הפרוקסי דחה את שיטות האימות שהוצעו");
    public static string ProxyNeedsAuth => T("proxy requires authentication", "הפרוקסי דורש אימות");
    public static string ProxyNeedsUserPass => T("proxy requires a username and password", "הפרוקסי דורש שם משתמש וסיסמה");
    public static string ProxyUserPassTooLong => T("username/password too long for SOCKS5", "שם המשתמש או הסיסמה ארוכים מדי ל-SOCKS5");
    public static string ProxyAuthFailed => T("proxy authentication failed (bad username or password)",
        "האימות מול הפרוקסי נכשל (שם משתמש או סיסמה שגויים)");
    public static string ProxyUnsupportedAuth(byte method) =>
        T($"proxy chose an unsupported SOCKS5 auth method (0x{method:X2})", $"הפרוקסי בחר שיטת אימות SOCKS5 שאינה נתמכת ({Ltr($"0x{method:X2}")})");
    public static string ProxyHostTooLong => T("host name too long for SOCKS5", "שם המארח ארוך מדי ל-SOCKS5");
    public static string ProxyMalformedSocks5 => T("malformed SOCKS5 reply", "תשובת SOCKS5 פגומה");
    public static string ProxySocks5Refused(string why) => T("SOCKS5 proxy refused: ", "פרוקסי SOCKS5 סירב: ") + why;
    public static string ProxyBadReplyAddress => T("bad SOCKS5 reply address", "כתובת שגויה בתשובת SOCKS5");
    public static string Socks5Error(byte b) => b switch
    {
        0x01 => T("general failure", "כשל כללי"),
        0x02 => T("connection not allowed by ruleset", "החיבור אסור לפי הכללים"),
        0x03 => T("network unreachable", "הרשת אינה נגישה"),
        0x04 => T("host unreachable", "המארח אינו נגיש"),
        0x05 => T("connection refused", "החיבור נדחה"),
        0x06 => T("TTL expired", "פג תוקף ה-TTL"),
        0x07 => T("command not supported", "הפקודה אינה נתמכת"),
        0x08 => T("address type not supported", "סוג הכתובת אינו נתמך"),
        _ => T($"code 0x{b:X2}", $"קוד {Ltr($"0x{b:X2}")}"),
    };
    public static string ProxySocks4NoIpv6 => T("SOCKS4 cannot route an IPv6 address (use SOCKS5)", "SOCKS4 לא יכול לנתב כתובת IPv6 (השתמש ב-SOCKS5)");
    public static string ProxySocks4Rejected => T("SOCKS4 proxy rejected the request", "פרוקסי SOCKS4 דחה את הבקשה");
    public static string ProxySocks4NoIdentd => T("SOCKS4 proxy could not reach the identd client", "פרוקסי SOCKS4 לא הצליח להגיע ללקוח ה-identd");
    public static string ProxySocks4IdentdFailed => T("SOCKS4 proxy identd auth failed", "אימות ה-identd בפרוקסי SOCKS4 נכשל");
    public static string ProxySocks4Refused(byte code) => T($"SOCKS4 proxy refused (code 0x{code:X2})", $"פרוקסי SOCKS4 סירב (קוד {Ltr($"0x{code:X2}")})");
    public static string ProxyHttpAuth => T("HTTP proxy authentication required/failed (407)", "פרוקסי HTTP דורש אימות, או שהאימות נכשל (407)");
    public static string ProxyHttpRefused(int code, string reason) => T("HTTP proxy refused: ", "פרוקסי HTTP סירב: ") + Ltr($"{code} {reason}".Trim());
    public static string ProxyNotHttp => T("not an HTTP proxy (bad response)", "זה לא פרוקסי HTTP (תשובה שגויה)");
    public static string ProxyClosed => T("proxy closed the connection unexpectedly", "הפרוקסי סגר את החיבור באופן לא צפוי");
    public static string ProxyTooLarge => T("proxy response too large / malformed", "תשובת הפרוקסי גדולה מדי או פגומה");

    // =====================================================================
    // Self-test: which strings have no translation
    // =====================================================================

    /// <summary>Values that are the same in every language (technical tokens).</summary>
    private static readonly HashSet<string> SameEverywhere = new(StringComparer.Ordinal) { "APK", "API" };

    /// <summary>Each language other than English: the string properties that show the English text.</summary>
    public static List<string> SelfTest()
    {
        var failures = new List<string>();
        var saved = _current;
        try
        {
            var props = typeof(L).GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0).ToList();
            _current = 0;
            var english = props.ToDictionary(p => p.Name, p => (string)p.GetValue(null)!);
            for (int i = 1; i < Languages.Length; i++)
            {
                _current = i;
                foreach (var p in props)
                {
                    var v = (string)p.GetValue(null)!;
                    if (string.IsNullOrWhiteSpace(v)) failures.Add($"strings: {p.Name} is empty in {Languages[i].Code}");
                    else if (v == english[p.Name] && !SameEverywhere.Contains(v) && p.Name != nameof(MainTitle))
                        failures.Add($"strings: {p.Name} has no {Languages[i].Code} translation");
                }
            }
            if (Resolve("he") != "he" || Resolve("EN") != "en" || NormalizeSetting("fr") != Auto)
                failures.Add("strings: language setting resolution");
        }
        finally { _current = saved; }
        return failures;
    }
}
