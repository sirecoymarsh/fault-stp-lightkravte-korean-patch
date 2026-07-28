using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Naninovel;
using Naninovel.Commands;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class LightKravteKorean : BaseUnityPlugin
{
    public const string PluginGuid = "kr.cadriaclord.lightkravte.korean";
    public const string PluginName = "Fault StP LIGHTKRAVTE Korean";
    public const string PluginVersion = "1.0.1";

    internal static LightKravteKorean Instance;
    internal static ManualLogSource Log;

    private Harmony harmony;
    private ConfigEntry<bool> koreanActive;
    private ConfigEntry<bool> strictBuildCheck;
    private ConfigEntry<string> translationDirectory;
    private ConfigEntry<string> fontPath;
    private ConfigEntry<float> scanInterval;
    private ConfigEntry<bool> fitLongDialogue;
    private ConfigEntry<float> minFontScale;
    private ConfigEntry<bool> logTextRendering;

    private readonly Dictionary<PrintText, StringParameter> temporarilyReplaced =
        new Dictionary<PrintText, StringParameter>();
    private readonly HashSet<string> missingScriptLocations =
        new HashSet<string>(StringComparer.Ordinal);

    private IScriptPlayer scriptPlayer;
    private ILocalizationManager localizationManager;
    private ITextManager textManager;
    private Func<Command, UniTask> preExecutionTask;
    private Func<Command, UniTask> postExecutionTask;
    private bool engineHooksInstalled;
    private bool runtimeEnabled;
    private float nextScanAt;
    private int managedRefreshGeneration;
    private Action<string> pendingLocaleRefreshHandler;

    internal TranslationStore Translations { get; private set; }
    internal KoreanFontBridge Fonts { get; private set; }
    internal TextFitBridge TextFit { get; private set; }
    internal TextDiagnostics Diagnostics { get; private set; }
    internal bool KoreanActive
    {
        get { return runtimeEnabled && koreanActive != null && koreanActive.Value; }
    }

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        koreanActive = Config.Bind(
            "General",
            "KoreanActive",
            true,
            "Persist the repurposed zh-TW selector as Korean.");
        strictBuildCheck = Config.Bind(
            "Compatibility",
            "StrictBuildCheck",
            true,
            "Disable all runtime hooks when the trusted assembly hashes do not match.");
        translationDirectory = Config.Bind(
            "Data",
            "TranslationDirectory",
            "translations",
            "Directory containing Korean JSONL records. Relative paths are resolved beside this plugin.");
        fontPath = Config.Bind(
            "Font",
            "FontPath",
            "fonts\\Korean.ttf",
            "Optional TTF/OTF path. Relative paths are resolved beside this plugin; installed Korean fonts are the fallback.");
        scanInterval = Config.Bind(
            "Font",
            "ScanIntervalSeconds",
            0.35f,
            "Interval used to attach Korean font fallbacks to newly created UI objects.");
        fitLongDialogue = Config.Bind(
            "Layout",
            "FitLongDialogue",
            true,
            "Shrink Korean dialogue only as far as needed to keep long lines inside the authored printer area.");
        minFontScale = Config.Bind(
            "Layout",
            "MinFontScale",
            0.75f,
            "Lower bound for dialogue auto-sizing, relative to the authored font size. Range 0.50-1.00.");
        logTextRendering = Config.Bind(
            "Diagnostics",
            "LogTextRendering",
            false,
            "Log one line per visible Korean text object: font, Korean fallback state, sub-mesh atlas textures and any glyph the font chain cannot supply. Text rendering as boxes leaves a Warning naming the object.");

        string compatibilityError;
        if (!BuildGuard.VerifyTrustedBuild(strictBuildCheck.Value, out compatibilityError))
        {
            Logger.LogError(compatibilityError);
            Logger.LogError("Korean runtime patch disabled before installing hooks.");
            runtimeEnabled = false;
            return;
        }

        try
        {
            string pluginDirectory = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location);
            string translationsPath = ResolvePath(pluginDirectory, translationDirectory.Value);
            string resolvedFontPath = ResolvePath(pluginDirectory, fontPath.Value);

            Translations = TranslationStore.Load(translationsPath, Logger);
            Fonts = new KoreanFontBridge(resolvedFontPath, Translations.AllKoreanText, Logger);
            if (fitLongDialogue.Value)
                TextFit = new TextFitBridge(minFontScale.Value, Logger);
            if (logTextRendering.Value)
                Diagnostics = new TextDiagnostics(Logger);

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(LightKravteKorean).Assembly);
            runtimeEnabled = true;

            preExecutionTask = HandlePreExecution;
            postExecutionTask = HandlePostExecution;

            Engine.OnInitializationFinished += HandleEngineInitialized;
            Engine.OnDestroyed += HandleEngineDestroyed;
            SceneManager.sceneLoaded += HandleSceneLoaded;

            if (Engine.Initialized)
                HandleEngineInitialized();

            Logger.LogInfo(
                "Runtime patch ready. script=" + Translations.ScriptCount +
                ", managed=" + Translations.ManagedCount +
                ", source=" + Translations.SourceCount +
                ", active=" + koreanActive.Value);
        }
        catch (Exception exception)
        {
            runtimeEnabled = false;
            Logger.LogError("Runtime patch initialization failed: " + exception);
            if (harmony != null)
                harmony.UnpatchSelf();
        }
    }

    private static string ResolvePath(string baseDirectory, string configuredPath)
    {
        if (String.IsNullOrEmpty(configuredPath))
            return baseDirectory;
        if (Path.IsPathRooted(configuredPath))
            return Path.GetFullPath(configuredPath);
        return Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
    }

    private void HandleEngineInitialized()
    {
        if (!runtimeEnabled)
            return;

        try
        {
            DetachEngineHooks();
            scriptPlayer = Engine.GetService<IScriptPlayer>();
            localizationManager = Engine.GetService<ILocalizationManager>();
            textManager = Engine.GetService<ITextManager>();

            if (scriptPlayer != null)
            {
                scriptPlayer.AddPreExecutionTask(preExecutionTask);
                scriptPlayer.AddPostExecutionTask(postExecutionTask);
                engineHooksInstalled = true;
            }

            if (KoreanActive &&
                localizationManager != null &&
                !String.Equals(localizationManager.SelectedLocale, "ja", StringComparison.Ordinal))
            {
                PrepareManagedTextRefreshAfterLocaleChange(
                    "ja",
                    "engine initialization locale change");
                UniTask.Void(delegate
                {
                    return localizationManager.SelectLocaleAsync("ja");
                });
            }
            else
            {
                ScheduleManagedTextRefresh("engine initialization");
            }

            RefreshRuntimeObjects(true);
            Logger.LogInfo("Naninovel script hooks installed.");
        }
        catch (Exception exception)
        {
            Logger.LogError("Failed to attach Naninovel hooks: " + exception);
        }
    }

    private void HandleEngineDestroyed()
    {
        RestoreTemporaryCommands();
        CancelPendingLocaleRefresh();
        engineHooksInstalled = false;
        scriptPlayer = null;
        localizationManager = null;
        textManager = null;
        managedRefreshGeneration++;
    }

    private void DetachEngineHooks()
    {
        if (!engineHooksInstalled || scriptPlayer == null)
            return;

        try
        {
            scriptPlayer.RemovePreExecutionTask(preExecutionTask);
            scriptPlayer.RemovePostExecutionTask(postExecutionTask);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Failed to detach previous Naninovel hooks: " + exception.Message);
        }
        engineHooksInstalled = false;
    }

    private UniTask HandlePreExecution(Command command)
    {
        PrintText print = command as PrintText;
        if (print == null)
            return UniTask.CompletedTask;

        RestoreTemporaryCommand(print);
        if (!KoreanActive)
            return UniTask.CompletedTask;

        string originalText = print.Text;
        string translated;
        if (!Translations.TryGetScript(print.PlaybackSpot, originalText, out translated))
        {
            PlaybackSpot spot = print.PlaybackSpot;
            if (spot.Valid)
            {
                string location = spot.ScriptName + ":" + spot.LineIndex;
                if (missingScriptLocations.Add(location))
                    Logger.LogDebug("No Korean script record for " + location);
            }
            return UniTask.CompletedTask;
        }

        temporarilyReplaced.Add(print, print.Text);
        print.Text = translated;
        return UniTask.CompletedTask;
    }

    private UniTask HandlePostExecution(Command command)
    {
        PrintText print = command as PrintText;
        if (print != null)
            RestoreTemporaryCommand(print);
        return UniTask.CompletedTask;
    }

    private void RestoreTemporaryCommand(PrintText print)
    {
        StringParameter original;
        if (!temporarilyReplaced.TryGetValue(print, out original))
            return;
        print.Text = original;
        temporarilyReplaced.Remove(print);
    }

    private void RestoreTemporaryCommands()
    {
        foreach (KeyValuePair<PrintText, StringParameter> pair in temporarilyReplaced)
        {
            if (pair.Key != null)
                pair.Key.Text = pair.Value;
        }
        temporarilyReplaced.Clear();
    }

    internal void SetKoreanActive(bool active, string reason)
    {
        if (!runtimeEnabled || koreanActive == null)
            return;

        if (koreanActive.Value != active)
        {
            koreanActive.Value = active;
            Config.Save();
            Logger.LogInfo("Korean mode " + (active ? "enabled" : "disabled") + " by " + reason + ".");
        }

        if (!active)
            RestoreTemporaryCommands();

        RefreshRuntimeObjects(true);
    }

    internal bool TryGetManaged(string category, string key, out string translated)
    {
        translated = null;
        return KoreanActive &&
               Translations != null &&
               Translations.TryGetManaged(category, key, out translated);
    }

    internal bool TryGetCharacterDisplayName(
        string characterId,
        out string translated)
    {
        translated = null;
        if (String.IsNullOrEmpty(characterId))
            return false;

        if (TryGetManaged("CharacterNames", characterId, out translated))
            return true;

        // One script line uses the visible Japanese role name as AuthorId
        // instead of the configured `med` character key.
        if (String.Equals(characterId, "医療班", StringComparison.Ordinal))
            return TryGetManaged("CharacterNames", "med", out translated);

        return false;
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (runtimeEnabled)
            RefreshRuntimeObjects(true);
    }

    private void ScheduleManagedTextRefresh(string reason)
    {
        if (!runtimeEnabled || !Engine.Initialized)
            return;

        if (textManager == null)
            textManager = Engine.GetService<ITextManager>();
        if (textManager == null)
        {
            Logger.LogWarning("Managed-text refresh skipped; ITextManager is unavailable.");
            return;
        }

        int generation = ++managedRefreshGeneration;
        StartCoroutine(RefreshManagedTextCoroutine(generation, reason));
    }

    internal int PrepareManagedTextRefreshAfterLocaleChange(
        string expectedLocale,
        string reason)
    {
        if (!runtimeEnabled || !Engine.Initialized || String.IsNullOrEmpty(expectedLocale))
            return -1;

        if (localizationManager == null)
            localizationManager = Engine.GetService<ILocalizationManager>();
        if (localizationManager == null)
        {
            Logger.LogWarning("Locale-settled managed-text refresh skipped; ILocalizationManager is unavailable.");
            return -1;
        }

        CancelPendingLocaleRefresh();
        int generation = ++managedRefreshGeneration;

        // Naninovel SelectLocaleAsync returns immediately without raising
        // OnLocaleChanged when the requested locale is already selected.
        // The SetOnClick postfix starts the refresh in that same-locale case.
        if (String.Equals(
            localizationManager.SelectedLocale,
            expectedLocale,
            StringComparison.Ordinal))
        {
            return generation;
        }

        Action<string> handler = null;
        handler = delegate(string selectedLocale)
        {
            if (localizationManager != null && handler != null)
                localizationManager.OnLocaleChanged -= handler;
            if (ReferenceEquals(pendingLocaleRefreshHandler, handler))
                pendingLocaleRefreshHandler = null;

            if (generation != managedRefreshGeneration || !runtimeEnabled)
                return;
            if (!String.Equals(selectedLocale, expectedLocale, StringComparison.Ordinal))
            {
                Logger.LogWarning(
                    "Locale changed to " + selectedLocale +
                    " while waiting for " + expectedLocale +
                    "; managed-text refresh cancelled.");
                return;
            }

            // LocalizationManager invokes OnLocaleChanged only after every
            // registered change-locale task has completed, so this apply cannot
            // race the game's own TextManager.ApplyManagedTextAsync path.
            StartCoroutine(RefreshManagedTextCoroutine(generation, reason));
        };
        pendingLocaleRefreshHandler = handler;
        localizationManager.OnLocaleChanged += handler;
        return 0;
    }

    internal void CompleteSameLocaleManagedTextRefresh(int generation, string reason)
    {
        if (generation <= 0 ||
            generation != managedRefreshGeneration ||
            !runtimeEnabled)
        {
            return;
        }

        StartCoroutine(RefreshManagedTextCoroutine(generation, reason));
    }

    private void CancelPendingLocaleRefresh()
    {
        if (pendingLocaleRefreshHandler == null)
            return;
        if (localizationManager != null)
            localizationManager.OnLocaleChanged -= pendingLocaleRefreshHandler;
        pendingLocaleRefreshHandler = null;
    }

    private IEnumerator RefreshManagedTextCoroutine(int generation, string reason)
    {
        UniTask task;
        try
        {
            task = textManager.ApplyManagedTextAsync();
        }
        catch (Exception exception)
        {
            Logger.LogError("Managed-text apply could not start: " + exception);
            yield break;
        }

        UniTask.Awaiter awaiter = task.GetAwaiter();
        while (!awaiter.IsCompleted)
            yield return null;

        try
        {
            awaiter.GetResult();
        }
        catch (Exception exception)
        {
            Logger.LogError("Managed-text apply failed: " + exception);
            yield break;
        }

        if (generation != managedRefreshGeneration || !runtimeEnabled)
            yield break;

        // The plugin's engine-initialized callback can run before the
        // ManagedTextProvider callbacks that assign their textManager fields.
        // Defer the private refresh until the initialization event has returned.
        yield return null;
        if (generation != managedRefreshGeneration || !runtimeEnabled)
            yield break;

        InvokeManagedTextProviders();
        RefreshRuntimeObjects(true);
        Logger.LogDebug("Managed text refreshed after " + reason + ".");
    }

    private void InvokeManagedTextProviders()
    {
        MethodInfo invoke = AccessTools.Method(
            typeof(ManagedTextProvider),
            "InvokeValueChanged");
        if (invoke == null)
        {
            Logger.LogError("ManagedTextProvider.InvokeValueChanged was not found.");
            return;
        }

        ManagedTextProvider[] providers =
            Resources.FindObjectsOfTypeAll<ManagedTextProvider>();
        for (int index = 0; index < providers.Length; index++)
        {
            ManagedTextProvider provider = providers[index];
            // FindObjectsOfTypeAll also returns disabled prefab/asset providers;
            // InvokeValueChanged assumes HandleEngineInitialized has run.
            if (provider == null || !provider.isActiveAndEnabled)
                continue;
            try
            {
                invoke.Invoke(provider, null);
            }
            catch (Exception exception)
            {
                Logger.LogWarning(
                    "ManagedTextProvider refresh failed on " +
                    provider.name + ": " + exception.Message);
            }
        }
    }

    private void Update()
    {
        if (!runtimeEnabled)
            return;
        if (Time.unscaledTime < nextScanAt)
            return;

        float interval = scanInterval.Value;
        if (interval < 0.1f)
            interval = 0.1f;
        nextScanAt = Time.unscaledTime + interval;
        RefreshRuntimeObjects(false);
    }

    private void RefreshRuntimeObjects(bool force)
    {
        try
        {
            if (Fonts != null)
                Fonts.Refresh(KoreanActive, Translations, force);
            // Korean lines are wider than the authored Japanese ones, so long
            // dialogue can spill past the bottom of a fixed printer area.
            if (TextFit != null)
                TextFit.Refresh(KoreanActive);
            if (Diagnostics != null)
                Diagnostics.Scan(KoreanActive, Fonts);
            // The Korean selector remains visible while an original locale is
            // selected, so apply its label and font after the general font
            // bridge has restored the original-language UI fonts.
            SelectorBridge.Refresh(KoreanActive, Fonts);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Runtime UI refresh failed: " + exception.Message);
        }
    }

    private void OnDestroy()
    {
        managedRefreshGeneration++;
        RestoreTemporaryCommands();
        DetachEngineHooks();

        Engine.OnInitializationFinished -= HandleEngineInitialized;
        Engine.OnDestroyed -= HandleEngineDestroyed;
        SceneManager.sceneLoaded -= HandleSceneLoaded;

        if (TextFit != null)
            TextFit.Restore();
        if (Fonts != null)
            Fonts.Dispose();
        if (harmony != null)
            harmony.UnpatchSelf();

        runtimeEnabled = false;
        if (Instance == this)
            Instance = null;
    }
}

internal static class BuildGuard
{
    private const string ExpectedAssemblyCSharp =
        "7B7594F992C6F26CFBC0CD1FA92DC587109290F70F543CA90F306A4C488F7797";
    private const string ExpectedUtilities =
        "EFAAC90B12592609F673DC08D5CC164DB5D6C9E050F10755361AA3B68BF5F7F8";
    private const string ExpectedNaninovel =
        "E20198AE1932E6DEA7040ECF7F33F661308E99E983FC2C7F1F84AFDE30D631C1";

    internal static bool VerifyTrustedBuild(bool strict, out string message)
    {
        string managed = Path.Combine(Application.dataPath, "Managed");
        string[] names =
        {
            "Assembly-CSharp.dll",
            "Utilities.dll",
            "Elringus.Naninovel.Runtime.dll"
        };
        string[] expected =
        {
            ExpectedAssemblyCSharp,
            ExpectedUtilities,
            ExpectedNaninovel
        };

        StringBuilder errors = new StringBuilder();
        for (int index = 0; index < names.Length; index++)
        {
            string path = Path.Combine(managed, names[index]);
            if (!File.Exists(path))
            {
                errors.AppendLine("Missing trusted file: " + path);
                continue;
            }

            string actual = ComputeSha256(path);
            if (!String.Equals(actual, expected[index], StringComparison.OrdinalIgnoreCase))
            {
                errors.AppendLine(
                    names[index] + " SHA-256 mismatch; expected " +
                    expected[index] + ", got " + actual + ".");
            }
        }

        if (errors.Length == 0)
        {
            message = "Trusted LIGHTKRAVTE build verified.";
            return true;
        }

        message = errors.ToString().TrimEnd();
        if (strict)
            return false;

        LightKravteKorean.Log.LogWarning(message);
        LightKravteKorean.Log.LogWarning(
            "StrictBuildCheck is disabled; continuing on an unverified build.");
        return true;
    }

    private static string ComputeSha256(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 algorithm = SHA256.Create())
        {
            byte[] hash = algorithm.ComputeHash(stream);
            StringBuilder result = new StringBuilder(hash.Length * 2);
            for (int index = 0; index < hash.Length; index++)
                result.Append(hash[index].ToString("X2"));
            return result.ToString();
        }
    }
}

#pragma warning disable 0649
[Serializable]
internal sealed class TranslationRecord
{
    public string uid;
    public string ko;
    public string script;
    public int source_line;
    public int line;
    public int inline_index;
    public string category;
    public string key;
    public string source;
    public string ja;
}
#pragma warning restore 0649

internal sealed class TranslationStore
{
    private const char Separator = '\u001f';

    private readonly Dictionary<string, string> scripts =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> managed =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sources =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly StringBuilder allKoreanText = new StringBuilder();

    internal int ScriptCount { get { return scripts.Count; } }
    internal int ManagedCount { get { return managed.Count; } }
    internal int SourceCount { get { return sources.Count; } }
    internal string AllKoreanText { get { return allKoreanText.ToString(); } }

    internal static TranslationStore Load(string root, ManualLogSource logger)
    {
        TranslationStore store = new TranslationStore();
        if (!Directory.Exists(root))
        {
            logger.LogWarning("Translation directory does not exist yet: " + root);
            return store;
        }

        string[] files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            int lineNumber = 0;
            foreach (string line in File.ReadLines(files[fileIndex], Encoding.UTF8))
            {
                lineNumber++;
                if (String.IsNullOrWhiteSpace(line))
                    continue;

                TranslationRecord record;
                try
                {
                    record = JsonUtility.FromJson<TranslationRecord>(line);
                }
                catch (Exception exception)
                {
                    throw new InvalidDataException(
                        files[fileIndex] + ":" + lineNumber + " is not valid JSON.", exception);
                }

                if (record == null || String.IsNullOrEmpty(record.ko))
                    throw new InvalidDataException(
                        files[fileIndex] + ":" + lineNumber + " has an empty ko value.");

                store.AddRecord(record, files[fileIndex], lineNumber);
            }
        }

        return store;
    }

    private void AddRecord(TranslationRecord record, string file, int lineNumber)
    {
        bool indexed = false;

        if (!String.IsNullOrEmpty(record.script))
        {
            int sourceLine = record.source_line > 0 ? record.source_line : record.line;
            if (sourceLine > 0)
            {
                AddUnique(
                    scripts,
                    MakeScriptKey(record.script, sourceLine),
                    record.ko,
                    file,
                    lineNumber);
                indexed = true;
            }
        }

        if (!String.IsNullOrEmpty(record.category) && !String.IsNullOrEmpty(record.key))
        {
            AddUnique(
                managed,
                MakeManagedKey(record.category, record.key),
                record.ko,
                file,
                lineNumber);
            indexed = true;
        }

        if (!String.IsNullOrEmpty(record.uid))
        {
            string[] parts = record.uid.Split(':');
            int parsedLine;
            if (parts.Length >= 3 &&
                !String.Equals(parts[0], "managed", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(parts[0], "mt", StringComparison.OrdinalIgnoreCase) &&
                Int32.TryParse(parts[1], out parsedLine))
            {
                AddUnique(
                    scripts,
                    MakeScriptKey(parts[0], parsedLine),
                    record.ko,
                    file,
                    lineNumber);
                indexed = true;
            }
            else if (parts.Length >= 4 &&
                     (String.Equals(parts[0], "managed", StringComparison.OrdinalIgnoreCase) ||
                      String.Equals(parts[0], "mt", StringComparison.OrdinalIgnoreCase)))
            {
                string managedKey = String.Join(":", parts, 2, parts.Length - 3);
                AddUnique(
                    managed,
                    MakeManagedKey(parts[1], managedKey),
                    record.ko,
                    file,
                    lineNumber);
                indexed = true;
            }
        }

        string source = !String.IsNullOrEmpty(record.ja) ? record.ja : record.source;
        if (!String.IsNullOrEmpty(source))
        {
            AddUnique(sources, source, record.ko, file, lineNumber);
            indexed = true;
        }

        if (!indexed)
        {
            throw new InvalidDataException(
                file + ":" + lineNumber +
                " has no usable uid, script/line, managed category/key, or source.");
        }

        allKoreanText.Append(record.ko);
        allKoreanText.Append('\n');
    }

    private static void AddUnique(
        Dictionary<string, string> target,
        string key,
        string value,
        string file,
        int lineNumber)
    {
        string previous;
        if (target.TryGetValue(key, out previous))
        {
            if (!String.Equals(previous, value, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    file + ":" + lineNumber + " conflicts with duplicate key " + key + ".");
            }
            return;
        }
        target.Add(key, value);
    }

    internal bool TryGetScript(PlaybackSpot spot, string currentSource, out string translated)
    {
        if (spot.Valid &&
            scripts.TryGetValue(MakeScriptKey(spot.ScriptName, spot.LineIndex), out translated))
            return true;

        if (!String.IsNullOrEmpty(currentSource) &&
            sources.TryGetValue(currentSource, out translated))
            return true;

        translated = null;
        return false;
    }

    internal bool TryGetManaged(string category, string key, out string translated)
    {
        if (String.IsNullOrEmpty(category) || String.IsNullOrEmpty(key))
        {
            translated = null;
            return false;
        }
        return managed.TryGetValue(MakeManagedKey(category, key), out translated);
    }

    internal bool TryGetSource(string source, out string translated)
    {
        if (String.IsNullOrEmpty(source))
        {
            translated = null;
            return false;
        }
        return sources.TryGetValue(source, out translated);
    }

    private static string MakeScriptKey(string script, int sourceLine)
    {
        return script + ":" + sourceLine;
    }

    private static string MakeManagedKey(string category, string key)
    {
        return category + Separator + key;
    }
}

internal sealed class KoreanFontBridge : IDisposable
{
    private const string HangulProbe = "가나다라마바사아자차카타파하한글힣";

    // 4096 / (72 + 2 * 8) = 46 glyphs per row, so a single page holds ~2,100
    // glyphs against a Korean closure of ~1,200.
    private const int KoreanAtlasSize = 4096;
    private const int KoreanAtlasSamplingSize = 72;
    private const int KoreanAtlasPadding = 8;

    private static readonly string[] PreferredFontNames =
    {
        "Malgun Gothic",
        "맑은 고딕",
        "Noto Sans CJK KR",
        "Noto Sans KR",
        "NanumGothic"
    };

    private static readonly string[] PreferredFontFileTokens =
    {
        "malgun",
        "malgun",
        "notosanscjkkr",
        "notosanskr",
        "nanumgothic"
    };

    private readonly ManualLogSource logger;
    private readonly Dictionary<Text, Font> originalUguiFonts =
        new Dictionary<Text, Font>();
    private readonly Dictionary<Text, Font> selectorUguiFonts =
        new Dictionary<Text, Font>();
    private readonly Dictionary<TMP_Text, TMP_FontAsset> selectorTmpPrimaryFonts =
        new Dictionary<TMP_Text, TMP_FontAsset>();
    private readonly HashSet<TMP_FontAsset> modifiedTmpFonts =
        new HashSet<TMP_FontAsset>();
    private readonly HashSet<TMP_FontAsset> attachedThisPass =
        new HashSet<TMP_FontAsset>();
    private readonly HashSet<TMP_FontAsset> selectorTmpFonts =
        new HashSet<TMP_FontAsset>();
    private readonly HashSet<char> unavailableRuntimeGlyphs =
        new HashSet<char>();

    private Font koreanFont;
    private Font koreanTmpSourceFont;
    private TMP_FontAsset koreanTmpFont;

    internal TMP_FontAsset KoreanTmpFont { get { return koreanTmpFont; } }
    internal Font KoreanUguiFont { get { return koreanFont; } }
    private string koreanFontSource;
    private bool warnedUnavailable;
    private bool warnedUguiUnavailable;

    internal KoreanFontBridge(
        string configuredFontPath,
        string glyphClosure,
        ManualLogSource logger)
    {
        this.logger = logger;
        string[] installedNames;
        string[] installedPaths;
        try
        {
            installedNames = Font.GetOSInstalledFontNames();
            installedPaths = Font.GetPathsToOSFonts();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Installed font enumeration failed: " + exception.Message);
            installedNames = new string[0];
            installedPaths = new string[0];
        }
        if (installedNames == null)
            installedNames = new string[0];
        if (installedPaths == null)
            installedPaths = new string[0];

        // TMP 2.1.x needs a source face backed by an actual font file.
        // A legacy OS dynamic Font is valid for uGUI, but FontEngine cannot
        // use it as the source of a dynamic TMP_FontAsset.
        koreanTmpFont = LoadTmpFont(configuredFontPath, installedPaths);
        koreanFont = LoadUguiFont(installedNames);
        if (koreanTmpFont == null)
        {
            logger.LogError(
                "No Korean-capable file-backed TMP font could be created.");
            return;
        }

        if (!String.IsNullOrEmpty(glyphClosure))
        {
            string koreanGlyphClosure = ExtractKoreanGlyphs(glyphClosure);
            string missing;
            bool complete = koreanTmpFont.TryAddCharacters(
                koreanGlyphClosure,
                out missing,
                true);
            if (!complete && !String.IsNullOrEmpty(missing))
                logger.LogWarning(
                    "Korean TMP prewarm missing glyphs: " + Preview(missing, 80));

            // More than one atlas page means the revealable printer will serve
            // some glyphs from a page its cached materials do not cover.
            int pages = koreanTmpFont.atlasTextureCount;
            string atlasReport =
                "Korean TMP atlas: pages=" + pages +
                ", size=" + koreanTmpFont.atlasWidth + "x" + koreanTmpFont.atlasHeight +
                ", sampling=" + koreanTmpFont.faceInfo.pointSize +
                ", glyphs=" + koreanGlyphClosure.Length;
            if (pages > 1)
                logger.LogWarning(atlasReport + " (multi-page; dialogue glyphs may render as boxes)");
            else
                logger.LogInfo(atlasReport);
        }

        logger.LogInfo(
            "Korean font bridge ready: TMP=" + koreanFontSource +
            ", UGUI=" +
            (koreanFont != null ? koreanFont.name : "unavailable"));
    }

    private TMP_FontAsset LoadTmpFont(
        string configuredFontPath,
        string[] installedPaths)
    {
        HashSet<string> attemptedPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!String.IsNullOrEmpty(configuredFontPath) &&
            File.Exists(configuredFontPath))
        {
            TMP_FontAsset external = TryCreateTmpFontFromPath(
                configuredFontPath,
                true,
                attemptedPaths);
            if (external != null)
            {
                logger.LogInfo(
                    "Loaded external Korean TMP font: " + koreanFontSource);
                return external;
            }
            logger.LogWarning(
                "External font could not populate the TMP Hangul probe: " +
                configuredFontPath);
        }

        // Prefer known Korean filenames. The names and path arrays returned by
        // Unity are not guaranteed to be aligned, so path tokens are safer.
        for (int preferredIndex = 0;
             preferredIndex < PreferredFontFileTokens.Length;
             preferredIndex++)
        {
            for (int pathIndex = 0; pathIndex < installedPaths.Length; pathIndex++)
            {
                string normalizedFileName = NormalizeFontName(
                    Path.GetFileNameWithoutExtension(installedPaths[pathIndex]));
                if (normalizedFileName.IndexOf(
                    PreferredFontFileTokens[preferredIndex],
                    StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                TMP_FontAsset fileFont = TryCreateTmpFontFromPath(
                    installedPaths[pathIndex],
                    true,
                    attemptedPaths);
                if (fileFont != null)
                {
                    logger.LogInfo(
                        "Using installed Korean TMP font: " +
                        PreferredFontNames[preferredIndex] +
                        " (" + koreanFontSource + ")");
                    return fileFont;
                }
            }
        }

        // Last resort: probe remaining installed font files by asking TMP to
        // populate a small Hangul closure. The first complete face wins.
        for (int pathIndex = 0; pathIndex < installedPaths.Length; pathIndex++)
        {
            TMP_FontAsset candidate = TryCreateTmpFontFromPath(
                installedPaths[pathIndex],
                false,
                attemptedPaths);
            if (candidate != null)
            {
                logger.LogInfo(
                    "Using discovered Korean-capable TMP font: " +
                    koreanFontSource);
                return candidate;
            }
        }
        return null;
    }

    private TMP_FontAsset TryCreateTmpFontFromPath(
        string path,
        bool logFailure,
        HashSet<string> attemptedPaths)
    {
        if (String.IsNullOrEmpty(path) ||
            attemptedPaths.Contains(path) ||
            !File.Exists(path))
            return null;

        string fullPath;
        Font sourceFont;
        TMP_FontAsset asset;
        try
        {
            fullPath = Path.GetFullPath(path);
            attemptedPaths.Add(fullPath);
            sourceFont = new Font(fullPath);
            // TMP's default dynamic atlas is a single 1024x1024 page rendered at
            // 90pt, which holds only about eighty Hangul glyphs. The remaining
            // glyphs spill onto extra atlas pages. Naninovel's revealable
            // printer keeps its own material cache while applying the reveal
            // effect, so a late fallback or a changed atlas/material layout can
            // leave its cached material state out of step with TMP's rebuilt
            // sub-meshes. One large page keeps the whole Korean closure on
            // atlas 0 and removes the multi-page trigger.
            asset = TMP_FontAsset.CreateFontAsset(
                sourceFont,
                KoreanAtlasSamplingSize,
                KoreanAtlasPadding,
                UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                KoreanAtlasSize,
                KoreanAtlasSize,
                AtlasPopulationMode.Dynamic,
                true);
        }
        catch (Exception exception)
        {
            if (logFailure)
            {
                logger.LogWarning(
                    "TMP font candidate threw [" + path + "]: " +
                    exception.Message);
            }
            return null;
        }

        if (asset == null)
        {
            UnityEngine.Object.Destroy(sourceFont);
            if (logFailure)
                logger.LogWarning("TMP font candidate could not load: " + fullPath);
            return null;
        }

        string missing;
        bool complete = false;
        try
        {
            complete = asset.TryAddCharacters(HangulProbe, out missing, true);
        }
        catch (Exception exception)
        {
            missing = HangulProbe;
            if (logFailure)
                logger.LogWarning(
                    "TMP Hangul probe threw [" + fullPath + "]: " +
                    exception.Message);
        }
        if (!complete || !String.IsNullOrEmpty(missing))
        {
            UnityEngine.Object.Destroy(asset);
            UnityEngine.Object.Destroy(sourceFont);
            if (logFailure)
            {
                logger.LogDebug(
                    "TMP font candidate lacks Hangul [" + fullPath +
                    "], missing=" + Preview(missing, 32));
            }
            return null;
        }

        asset.name = "LIGHTKRAVTE_Korean_Dynamic";
        koreanTmpSourceFont = sourceFont;
        koreanFontSource = fullPath;
        return asset;
    }

    private Font LoadUguiFont(string[] installedNames)
    {
        for (int preferredIndex = 0;
             preferredIndex < PreferredFontNames.Length;
             preferredIndex++)
        {
            for (int installedIndex = 0;
                 installedIndex < installedNames.Length;
                 installedIndex++)
            {
                if (!String.Equals(
                    installedNames[installedIndex],
                    PreferredFontNames[preferredIndex],
                    StringComparison.OrdinalIgnoreCase))
                    continue;
                Font candidate = TryCreateUguiFont(installedNames[installedIndex]);
                if (candidate != null)
                {
                    logger.LogInfo(
                        "Using installed Korean uGUI font: " +
                        installedNames[installedIndex]);
                    return candidate;
                }
            }
        }

        for (int installedIndex = 0;
             installedIndex < installedNames.Length;
             installedIndex++)
        {
            Font candidate = TryCreateUguiFont(installedNames[installedIndex]);
            if (candidate != null)
            {
                logger.LogInfo(
                    "Using discovered Korean-capable uGUI font: " +
                    installedNames[installedIndex]);
                return candidate;
            }
        }
        return null;
    }

    private static Font TryCreateUguiFont(string familyName)
    {
        if (String.IsNullOrEmpty(familyName))
            return null;
        Font candidate = null;
        try
        {
            candidate = Font.CreateDynamicFontFromOSFont(familyName, 32);
            if (HasHangulProbe(candidate))
                return candidate;
        }
        catch
        {
        }
        if (candidate != null)
            UnityEngine.Object.Destroy(candidate);
        return null;
    }

    private static bool HasHangulProbe(Font font)
    {
        if (font == null)
            return false;
        try
        {
            font.RequestCharactersInTexture(HangulProbe, 32);
            for (int index = 0; index < HangulProbe.Length; index++)
            {
                if (!font.HasCharacter(HangulProbe[index]))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeFontName(string value)
    {
        if (String.IsNullOrEmpty(value))
            return String.Empty;
        StringBuilder normalized = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (Char.IsLetterOrDigit(character))
                normalized.Append(Char.ToLowerInvariant(character));
        }
        return normalized.ToString();
    }

    internal void Refresh(
        bool active,
        TranslationStore translations,
        bool force)
    {
        if (koreanFont == null && koreanTmpFont == null)
        {
            if (!warnedUnavailable)
            {
                warnedUnavailable = true;
                logger.LogWarning("Korean font refresh skipped because font initialization failed.");
            }
            return;
        }

        if (!active)
        {
            Restore();
            return;
        }

        if (koreanTmpFont != null)
        {
            attachedThisPass.Clear();
            TMP_Text[] tmpTexts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int index = 0; index < tmpTexts.Length; index++)
            {
                TMP_Text text = tmpTexts[index];
                if (text == null)
                    continue;

                string translated;
                if (translations != null &&
                    translations.TryGetSource(text.text, out translated))
                    text.text = translated;

                TMP_FontAsset primary;
                if (AttachTmpFallback(text, out primary))
                    attachedThisPass.Add(primary);
            }

            // The engine assigns locale fonts to printers after the UI is built,
            // so a line can be laid out while its font asset still has no Korean
            // fallback. TMP keeps that mesh until something dirties it, which is
            // why Hangul intermittently rendered as blank boxes. Force a rebuild
            // of every text that uses a font asset patched on this pass.
            if (attachedThisPass.Count > 0)
                ForceRebuild(tmpTexts);
        }

        if (koreanFont == null)
        {
            if (!warnedUguiUnavailable)
            {
                warnedUguiUnavailable = true;
                logger.LogWarning(
                    "Korean uGUI font is unavailable; TMP fallback remains active.");
            }
            return;
        }

        Text[] uguiTexts = Resources.FindObjectsOfTypeAll<Text>();
        for (int index = 0; index < uguiTexts.Length; index++)
        {
            Text text = uguiTexts[index];
            if (text == null)
                continue;

            string translated;
            if (translations != null &&
                translations.TryGetSource(text.text, out translated))
                text.text = translated;

            if (!ContainsHangul(text.text))
                continue;
            if (!originalUguiFonts.ContainsKey(text))
                originalUguiFonts.Add(text, text.font);
            if (text.font != koreanFont)
                text.font = koreanFont;
        }
    }

    // Called by the revealable-text prefix before TMP receives a Korean line.
    // The periodic object scan remains as a fallback for components created or
    // re-fonted by other UI paths, but dialogue layout no longer races it.
    internal bool EnsureTmpFallback(TMP_Text text)
    {
        TMP_FontAsset primary;
        AttachTmpFallback(text, out primary);
        return primary != null &&
            (primary == koreanTmpFont ||
             (primary.fallbackFontAssetTable != null &&
              primary.fallbackFontAssetTable.Contains(koreanTmpFont)));
    }

    internal bool PrepareRevealableText(
        Naninovel.UI.RevealableTMProText text,
        string value)
    {
        if (text == null ||
            koreanTmpFont == null ||
            !ContainsHangul(value) ||
            !EnsureTmpFallback(text))
            return false;

        // The constructor prewarms the complete frozen translation closure.
        // Only add genuinely new Hangul produced by a runtime variable; adding
        // the whole Hangul block would needlessly force extra atlas pages.
        string glyphs = ExtractKoreanGlyphs(value);
        StringBuilder needed = new StringBuilder();
        for (int index = 0; index < glyphs.Length; index++)
        {
            char character = glyphs[index];
            if (!unavailableRuntimeGlyphs.Contains(character) &&
                !koreanTmpFont.HasCharacter(character, false, false))
                needed.Append(character);
        }
        if (needed.Length == 0)
            return true;

        string missing;
        bool complete = koreanTmpFont.TryAddCharacters(
            needed.ToString(),
            out missing,
            true);
        if (!complete && !String.IsNullOrEmpty(missing))
        {
            for (int index = 0; index < missing.Length; index++)
                unavailableRuntimeGlyphs.Add(missing[index]);
            logger.LogWarning(
                "Revealable TMP missing Korean glyphs: " +
                Preview(missing, 40));
        }
        if (koreanTmpFont.atlasTextureCount > 1)
        {
            logger.LogWarning(
                "Korean TMP runtime glyph expansion created " +
                koreanTmpFont.atlasTextureCount +
                " atlas pages; revealable dialogue may regress.");
        }
        return true;
    }

    // CustomUI.SetFont is the exact locale-font writer for Naninovel printers.
    // Its postfix calls this before a later frame can reveal text with a newly
    // assigned primary font that has not yet been seen by the periodic scan.
    internal void RefreshCustomUiFont(Naninovel.UI.CustomUI ui)
    {
        if (ui == null || koreanTmpFont == null)
            return;

        Naninovel.UI.RevealableTMProText[] texts =
            ui.GetComponentsInChildren<Naninovel.UI.RevealableTMProText>(true);
        for (int index = 0; index < texts.Length; index++)
        {
            Naninovel.UI.RevealableTMProText text = texts[index];
            if (text == null)
                continue;

            try
            {
                if (!EnsureTmpFallback(text))
                    continue;
                RevealableTmpMaterialRepair.Invalidate(text);
                text.SetAllDirty();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Korean printer font refresh failed on " + text.name +
                    ": " + exception.Message);
            }
        }
    }

    private bool AttachTmpFallback(TMP_Text text, out TMP_FontAsset primary)
    {
        primary = text == null ? null : text.font;
        if (koreanTmpFont == null || primary == null || primary == koreanTmpFont)
            return false;

        List<TMP_FontAsset> fallbacks = primary.fallbackFontAssetTable;
        if (fallbacks == null)
        {
            fallbacks = new List<TMP_FontAsset>();
            primary.fallbackFontAssetTable = fallbacks;
        }
        if (fallbacks.Contains(koreanTmpFont))
            return false;

        fallbacks.Add(koreanTmpFont);
        modifiedTmpFonts.Add(primary);
        return true;
    }

    private void ForceRebuild(TMP_Text[] tmpTexts)
    {
        for (int index = 0; index < tmpTexts.Length; index++)
        {
            TMP_Text text = tmpTexts[index];
            if (text == null)
                continue;
            TMP_FontAsset primary = text.font;
            if (primary == null || !attachedThisPass.Contains(primary))
                continue;
            if (String.IsNullOrEmpty(text.text) || !ContainsHangul(text.text))
                continue;

            try
            {
                Naninovel.UI.RevealableTMProText revealable =
                    text as Naninovel.UI.RevealableTMProText;
                if (revealable != null)
                    RevealableTmpMaterialRepair.Invalidate(revealable);
                text.ForceMeshUpdate(true, true);
                if (revealable != null)
                {
                    RevealableTmpMaterialRepair.Invalidate(revealable);
                    revealable.SetAllDirty();
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Korean fallback rebuild failed on " + text.name + ": " +
                    exception.Message);
            }
        }
    }

    internal void EnsureSelectorLabelFont(Text text)
    {
        if (text == null || koreanFont == null)
            return;

        if (!selectorUguiFonts.ContainsKey(text))
            selectorUguiFonts.Add(text, text.font);
        if (text.font != koreanFont)
            text.font = koreanFont;
    }

    internal void EnsureSelectorLabelFont(TMP_Text text)
    {
        if (text == null || koreanTmpFont == null)
            return;

        TMP_FontAsset primary = text.font;
        if (primary == null)
        {
            if (!selectorTmpPrimaryFonts.ContainsKey(text))
                selectorTmpPrimaryFonts.Add(text, null);
            text.font = koreanTmpFont;
            return;
        }
        if (primary == koreanTmpFont)
            return;

        List<TMP_FontAsset> fallbacks = primary.fallbackFontAssetTable;
        if (fallbacks == null)
        {
            fallbacks = new List<TMP_FontAsset>();
            primary.fallbackFontAssetTable = fallbacks;
        }
        if (!fallbacks.Contains(koreanTmpFont))
        {
            fallbacks.Add(koreanTmpFont);
            selectorTmpFonts.Add(primary);
        }
    }

    private void Restore()
    {
        foreach (KeyValuePair<Text, Font> pair in originalUguiFonts)
        {
            if (pair.Key != null)
                pair.Key.font = pair.Value;
        }
        originalUguiFonts.Clear();

        foreach (TMP_FontAsset font in modifiedTmpFonts)
        {
            if (font == null || font.fallbackFontAssetTable == null)
                continue;
            font.fallbackFontAssetTable.Remove(koreanTmpFont);
        }
        modifiedTmpFonts.Clear();
    }

    public void Dispose()
    {
        Restore();
        foreach (KeyValuePair<Text, Font> pair in selectorUguiFonts)
        {
            if (pair.Key != null)
                pair.Key.font = pair.Value;
        }
        selectorUguiFonts.Clear();
        foreach (TMP_FontAsset font in selectorTmpFonts)
        {
            if (font == null || font.fallbackFontAssetTable == null)
                continue;
            font.fallbackFontAssetTable.Remove(koreanTmpFont);
        }
        selectorTmpFonts.Clear();
        foreach (
            KeyValuePair<TMP_Text, TMP_FontAsset> pair
            in selectorTmpPrimaryFonts)
        {
            if (pair.Key != null)
                pair.Key.font = pair.Value;
        }
        selectorTmpPrimaryFonts.Clear();
        if (koreanTmpFont != null)
            UnityEngine.Object.Destroy(koreanTmpFont);
        if (koreanFont != null)
            UnityEngine.Object.Destroy(koreanFont);
        if (koreanTmpSourceFont != null &&
            !ReferenceEquals(koreanTmpSourceFont, koreanFont))
            UnityEngine.Object.Destroy(koreanTmpSourceFont);
        koreanTmpFont = null;
        koreanFont = null;
        koreanTmpSourceFont = null;
    }

    internal static bool ContainsHangul(string value)
    {
        if (String.IsNullOrEmpty(value))
            return false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if ((character >= '\u1100' && character <= '\u11ff') ||
                (character >= '\u3130' && character <= '\u318f') ||
                (character >= '\uac00' && character <= '\ud7a3'))
                return true;
        }
        return false;
    }

    private static string ExtractKoreanGlyphs(string value)
    {
        if (String.IsNullOrEmpty(value))
            return String.Empty;
        HashSet<char> seen = new HashSet<char>();
        StringBuilder result = new StringBuilder();
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            bool korean =
                (character >= '\u1100' && character <= '\u11ff') ||
                (character >= '\u3130' && character <= '\u318f') ||
                (character >= '\uac00' && character <= '\ud7a3');
            if (korean && seen.Add(character))
                result.Append(character);
        }
        return result.ToString();
    }

    private static string Preview(string value, int maximum)
    {
        if (String.IsNullOrEmpty(value) || value.Length <= maximum)
            return value;
        return value.Substring(0, maximum) + "…";
    }
}

internal static class RevealableTmpMaterialRepair
{
    private static readonly FieldInfo CachedMaterialsField =
        AccessTools.Field(
            typeof(Naninovel.UI.RevealableTMProText),
            "cachedFontMaterials");
    private static bool warnedUnavailable;

    internal static Material[] Read(
        Naninovel.UI.RevealableTMProText text)
    {
        if (text == null || CachedMaterialsField == null)
            return null;
        return CachedMaterialsField.GetValue(text) as Material[];
    }

    internal static void Invalidate(
        Naninovel.UI.RevealableTMProText text)
    {
        if (text == null)
            return;
        if (CachedMaterialsField == null)
        {
            if (!warnedUnavailable)
            {
                warnedUnavailable = true;
                if (LightKravteKorean.Log != null)
                {
                    LightKravteKorean.Log.LogWarning(
                        "Revealable TMP material cache field was not found.");
                }
            }
            return;
        }
        CachedMaterialsField.SetValue(text, null);
    }
}

internal static class SelectorBridge
{
    private static bool refreshing;

    internal static void Refresh(bool active, KoreanFontBridge fonts)
    {
        // Enabling the dormant button invokes its Awake/InitializeManagers
        // path, whose Harmony postfix calls back into this bridge.
        if (refreshing)
            return;

        refreshing = true;
        try
        {
            RefreshCore(active, fonts);
        }
        finally
        {
            refreshing = false;
        }
    }

    private static void RefreshCore(bool active, KoreanFontBridge fonts)
    {
        LanguageWheelButtonController[] controllers =
            Resources.FindObjectsOfTypeAll<LanguageWheelButtonController>();
        for (int index = 0; index < controllers.Length; index++)
        {
            LanguageWheelButtonController controller = controllers[index];
            if (controller == null ||
                !String.Equals(
                    controller.name,
                    "TraditionalChineseButton",
                    StringComparison.Ordinal))
                continue;

            GameObject selectorObject = controller.gameObject;
            // Resources.FindObjectsOfTypeAll also returns the inactive prefab
            // asset. Only enable the instantiated Settings UI scene object.
            if (selectorObject == null || !selectorObject.scene.IsValid())
                continue;

            if (!controller.enabled)
                controller.enabled = true;

            // The shipped dormant button exactly overlaps the simplified
            // Chinese button at (250, -165). Give the repurposed selector an
            // unobstructed bottom-center position between the two lower
            // original-language choices.
            RectTransform selectorRect =
                selectorObject.GetComponent<RectTransform>();
            if (selectorRect != null)
                selectorRect.anchoredPosition = new Vector2(0f, -280f);

            bool enabledSelector = !selectorObject.activeSelf;
            if (enabledSelector)
                selectorObject.SetActive(true);

            Text[] texts = controller.GetComponentsInChildren<Text>(true);
            for (int textIndex = 0; textIndex < texts.Length; textIndex++)
            {
                texts[textIndex].text = "한국어";
                if (fonts != null)
                    fonts.EnsureSelectorLabelFont(texts[textIndex]);
            }

            TMP_Text[] tmpTexts = controller.GetComponentsInChildren<TMP_Text>(true);
            for (int textIndex = 0; textIndex < tmpTexts.Length; textIndex++)
            {
                tmpTexts[textIndex].text = "한국어";
                if (fonts != null)
                    fonts.EnsureSelectorLabelFont(tmpTexts[textIndex]);
            }

            Button koreanButton = controller.GetComponent<Button>();
            if (koreanButton != null && !koreanButton.interactable)
                koreanButton.interactable = true;

            Transform buttonParent = selectorObject.transform.parent;
            if (koreanButton != null && buttonParent != null)
            {
                Transform simplifiedObject =
                    buttonParent.Find("SimplifiedChineseButton");
                Transform japaneseObject =
                    buttonParent.Find("JapaneseButton");
                Transform englishObject =
                    buttonParent.Find("EnglishButton");
                Button simplifiedButton = simplifiedObject != null
                    ? simplifiedObject.GetComponent<Button>()
                    : null;
                Button japaneseButton = japaneseObject != null
                    ? japaneseObject.GetComponent<Button>()
                    : null;
                Button englishButton = englishObject != null
                    ? englishObject.GetComponent<Button>()
                    : null;
                if (simplifiedButton != null)
                {
                    Navigation navigation = simplifiedButton.navigation;
                    if (navigation.selectOnLeft != koreanButton)
                    {
                        navigation.selectOnLeft = koreanButton;
                        simplifiedButton.navigation = navigation;
                    }
                }
                if (japaneseButton != null)
                {
                    Navigation navigation = japaneseButton.navigation;
                    if (navigation.selectOnDown != koreanButton)
                    {
                        navigation.selectOnDown = koreanButton;
                        japaneseButton.navigation = navigation;
                    }
                }
                if (englishButton != null)
                {
                    Navigation navigation = englishButton.navigation;
                    if (navigation.selectOnRight != koreanButton)
                    {
                        navigation.selectOnRight = koreanButton;
                        englishButton.navigation = navigation;
                    }
                }

                Navigation koreanNavigation = koreanButton.navigation;
                bool koreanNavigationChanged = false;
                if (japaneseButton != null &&
                    koreanNavigation.selectOnUp != japaneseButton)
                {
                    koreanNavigation.selectOnUp = japaneseButton;
                    koreanNavigationChanged = true;
                }
                if (englishButton != null &&
                    koreanNavigation.selectOnLeft != englishButton)
                {
                    koreanNavigation.selectOnLeft = englishButton;
                    koreanNavigationChanged = true;
                }
                if (simplifiedButton != null &&
                    koreanNavigation.selectOnRight != simplifiedButton)
                {
                    koreanNavigation.selectOnRight = simplifiedButton;
                    koreanNavigationChanged = true;
                }
                if (koreanNavigationChanged)
                    koreanButton.navigation = koreanNavigation;
            }

            if (active)
            {
                LanguageWheelController wheel =
                    controller.GetComponentInParent<LanguageWheelController>();
                if (koreanButton != null && wheel != null)
                    wheel.SetCurrentLanguageButton(koreanButton);
            }

            if (enabledSelector && LightKravteKorean.Log != null)
                LightKravteKorean.Log.LogInfo(
                    "Korean zh-TW language selector enabled.");
        }
    }

    internal static bool ApplyWheelAnimationState(
        LanguageWheelController wheel,
        int state)
    {
        if (wheel == null)
            return true;

        Animator animator = wheel.GetComponent<Animator>();
        if (state != (int)Utilities.Locale.zhTW)
        {
            // Restore the shipped animator before every original-language
            // state and let the original method select its authored clip.
            if (animator != null && !animator.enabled)
                animator.enabled = true;
            return true;
        }

        Transform outerRing = wheel.transform.Find("outer_ring");
        if (animator == null || outerRing == null)
            return true;

        // The added Korean button is below the wheel at (0, -280); bind the
        // pointer to the corresponding authored-space rotation.
        animator.enabled = false;
        outerRing.localRotation = Quaternion.Euler(0f, 0f, 180f);
        return false;
    }
}

[HarmonyPatch(typeof(LanguageWheelController), "SetAnimationState")]
internal static class LanguageWheelPointerPatch
{
    [HarmonyPrefix]
    private static bool Prefix(LanguageWheelController __instance, int __0)
    {
        return SelectorBridge.ApplyWheelAnimationState(__instance, __0);
    }
}

[HarmonyPatch(typeof(LanguageWheelButtonController), "SetOnClick")]
internal static class LanguageWheelSelectionPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref Utilities.Locale __0, out int __state)
    {
        __state = -1;
        LightKravteKorean plugin = LightKravteKorean.Instance;
        if (plugin == null)
            return;

        string expectedLocale;
        if (__0 == Utilities.Locale.zhTW)
        {
            plugin.SetKoreanActive(true, "zh-TW selector");
            __0 = Utilities.Locale.ja;
            expectedLocale = "ja";
        }
        else
        {
            plugin.SetKoreanActive(false, "original-language selector");
            expectedLocale = LocaleCode(__0);
        }

        __state = plugin.PrepareManagedTextRefreshAfterLocaleChange(
            expectedLocale,
            "language selector");
    }

    [HarmonyPostfix]
    private static void Postfix(int __state)
    {
        LightKravteKorean plugin = LightKravteKorean.Instance;
        if (plugin != null)
        {
            plugin.CompleteSameLocaleManagedTextRefresh(
                __state,
                "same-locale language selector");
        }
    }

    private static string LocaleCode(Utilities.Locale locale)
    {
        if (locale == Utilities.Locale.en)
            return "en";
        if (locale == Utilities.Locale.zhCN)
            return "zh-CN";
        if (locale == Utilities.Locale.zhTW)
            return "zh-TW";
        return "ja";
    }
}

[HarmonyPatch(typeof(LanguageWheelButtonController), "InitializeManagers")]
internal static class LanguageWheelInitializationPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        LightKravteKorean plugin = LightKravteKorean.Instance;
        if (plugin != null)
            SelectorBridge.Refresh(plugin.KoreanActive, plugin.Fonts);
    }
}

[HarmonyPatch(typeof(TextManager), "GetRecordValue")]
internal static class ManagedTextLookupPatch
{
    [HarmonyPrefix]
    private static bool Prefix(string __0, string __1, ref string __result)
    {
        LightKravteKorean plugin = LightKravteKorean.Instance;
        string translated;
        if (plugin != null && plugin.TryGetManaged(__1, __0, out translated))
        {
            __result = translated;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(CharacterManager), "GetDisplayName")]
internal static class CharacterDisplayNamePatch
{
    [HarmonyPrefix]
    private static bool Prefix(string __0, ref string __result)
    {
        LightKravteKorean plugin = LightKravteKorean.Instance;
        string translated;
        if (plugin != null &&
            plugin.TryGetCharacterDisplayName(__0, out translated))
        {
            __result = translated;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(TextManager), "ApplyRecord")]
internal static class ManagedTextApplicationPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref ManagedTextRecord __0)
    {
        LightKravteKorean plugin = LightKravteKorean.Instance;
        string translated;
        if (plugin != null &&
            plugin.TryGetManaged(__0.Category, __0.Key, out translated))
        {
            // ManagedTextRecord(key, value, category), not (category, key, value).
            __0 = new ManagedTextRecord(__0.Key, translated, __0.Category);
        }
    }
}

[HarmonyPatch(typeof(TextManager), "GetAllRecords")]
internal static class ManagedTextCollectionPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        ref IReadOnlyCollection<ManagedTextRecord> __result)
    {
        LightKravteKorean plugin = LightKravteKorean.Instance;
        if (plugin == null || !plugin.KoreanActive || __result == null)
            return;

        List<ManagedTextRecord> mapped =
            new List<ManagedTextRecord>(__result.Count);
        foreach (ManagedTextRecord record in __result)
        {
            string translated;
            if (plugin.TryGetManaged(record.Category, record.Key, out translated))
                // ManagedTextRecord(key, value, category), not (category, key, value).
                mapped.Add(new ManagedTextRecord(record.Key, translated, record.Category));
            else
                mapped.Add(record);
        }
        __result = mapped;
    }
}

// Close the other side of the timing window: Naninovel can replace a printer's
// primary locale font after the UI object was created. Attach Korean fallback
// immediately after that exact writer instead of waiting for the global scan.
[HarmonyPatch(typeof(Naninovel.UI.CustomUI), "SetFont")]
internal static class PrinterFontPatch
{
    [HarmonyPostfix]
    private static void Postfix(Naninovel.UI.CustomUI __instance)
    {
        LightKravteKorean plugin = LightKravteKorean.Instance;
        if (plugin == null || !plugin.KoreanActive || plugin.Fonts == null)
            return;
        try
        {
            plugin.Fonts.RefreshCustomUiFont(__instance);
        }
        catch (Exception exception)
        {
            LightKravteKorean.Log.LogWarning(
                "Korean printer font writer repair failed: " +
                exception.Message);
        }
    }
}

// Naninovel assigns the line and immediately prepares the typewriter reveal.
// Attaching the fallback on the later periodic scan is therefore too late for
// the first layout of a newly created or re-fonted printer. Prepare the exact
// revealable TMP component before SetTextToReveal writes TMP_Text.text, and
// schedule a normal Canvas rebuild even when the same string is assigned.
[HarmonyPatch(
    typeof(Naninovel.UI.RevealableTMProText),
    "SetTextToReveal",
    new Type[] { typeof(string) })]
internal static class RevealableTmpTextPreparationPatch
{
    [HarmonyPrefix]
    private static void Prefix(
        Naninovel.UI.RevealableTMProText __instance,
        string __0,
        out bool __state)
    {
        __state = false;
        LightKravteKorean plugin = LightKravteKorean.Instance;
        if (plugin == null ||
            !plugin.KoreanActive ||
            plugin.Fonts == null)
            return;

        try
        {
            __state = plugin.Fonts.PrepareRevealableText(__instance, __0);
        }
        catch (Exception exception)
        {
            LightKravteKorean.Log.LogWarning(
                "Revealable TMP assignment preparation failed: " +
                exception.Message);
        }
    }

    [HarmonyPostfix]
    private static void Postfix(
        Naninovel.UI.RevealableTMProText __instance,
        bool __state)
    {
        if (!__state)
            return;
        try
        {
            RevealableTmpMaterialRepair.Invalidate(__instance);
            __instance.SetAllDirty();
        }
        catch (Exception exception)
        {
            LightKravteKorean.Log.LogWarning(
                "Revealable TMP assignment invalidation failed: " +
                exception.Message);
        }
    }
}

// At CanvasUpdate.PreRender, the primary font and final text are both known.
// Clear Naninovel's cache immediately before the original method builds TMP's
// mesh and then asks the reveal behaviour to acquire its material array.
[HarmonyPatch(
    typeof(Naninovel.UI.RevealableTMProText),
    "Rebuild",
    new Type[] { typeof(CanvasUpdate) })]
internal static class RevealableTmpRebuildPatch
{
    [HarmonyPrefix]
    private static void Prefix(
        Naninovel.UI.RevealableTMProText __instance,
        CanvasUpdate __0)
    {
        if (__0 != CanvasUpdate.PreRender)
            return;

        LightKravteKorean plugin = LightKravteKorean.Instance;
        if (plugin == null ||
            !plugin.KoreanActive ||
            plugin.Fonts == null)
            return;
        try
        {
            if (!plugin.Fonts.PrepareRevealableText(
                    __instance,
                    __instance.text))
                return;
            RevealableTmpMaterialRepair.Invalidate(__instance);
        }
        catch (Exception exception)
        {
            LightKravteKorean.Log.LogWarning(
                "Revealable TMP pre-render repair failed: " +
                exception.Message);
        }
    }
}

// Korean glyph runs are wider than the authored Japanese ones, so a line that
// the original fits on one row can wrap onto two. Printer text areas have a
// fixed height and TMP overflow is set to Overflow, so the surplus rows spill
// past the bottom of the dialogue area. Auto-sizing shrinks each printed line
// only as far as needed to stay inside the authored area; short lines keep the
// original size because the maximum is pinned to it.
internal sealed class TextFitBridge
{
    private sealed class TmpFitState
    {
        internal bool OriginalAutoSizing;
        internal float OriginalMin;
        internal float OriginalMax;
        internal float OriginalSize;
        internal float AppliedMin;
        internal float AppliedMax;
        internal float LastObservedSize;
    }

    private sealed class UguiFitState
    {
        internal bool OriginalBestFit;
        internal int OriginalMin;
        internal int OriginalMax;
        internal int AppliedMin;
        internal int AppliedMax;
        internal int LastObservedSize;
    }

    private readonly Dictionary<Naninovel.UI.RevealableTMProText, TmpFitState> tmpStates =
        new Dictionary<Naninovel.UI.RevealableTMProText, TmpFitState>();
    private readonly Dictionary<Naninovel.UI.RevealableUIText, UguiFitState> uguiStates =
        new Dictionary<Naninovel.UI.RevealableUIText, UguiFitState>();
    private readonly ManualLogSource logger;
    private readonly float minScale;

    internal TextFitBridge(float scale, ManualLogSource log)
    {
        logger = log;
        if (scale < 0.5f)
            scale = 0.5f;
        if (scale > 1f)
            scale = 1f;
        minScale = scale;
    }

    internal void Refresh(bool active)
    {
        if (!active)
        {
            Restore();
            return;
        }

        Naninovel.UI.RevealableTMProText[] tmpTexts =
            Resources.FindObjectsOfTypeAll<Naninovel.UI.RevealableTMProText>();
        for (int index = 0; index < tmpTexts.Length; index++)
            TrackTmp(tmpTexts[index]);

        Naninovel.UI.RevealableUIText[] uguiTexts =
            Resources.FindObjectsOfTypeAll<Naninovel.UI.RevealableUIText>();
        for (int index = 0; index < uguiTexts.Length; index++)
            TrackUgui(uguiTexts[index]);
    }

    private void TrackTmp(Naninovel.UI.RevealableTMProText text)
    {
        if (text == null)
            return;

        TmpFitState known;
        if (tmpStates.TryGetValue(text, out known))
        {
            // Remember what auto-sizing settled on so the font size hook can
            // tell an engine-driven change apart from our own shrinking.
            known.LastObservedSize = text.fontSize;
            return;
        }

        // Resources.FindObjectsOfTypeAll also returns prefab assets; only the
        // instantiated printers belong to a loaded scene.
        if (!text.gameObject.scene.IsValid())
            return;

        RectTransform rect = text.rectTransform;
        if (rect == null)
            return;

        float baseSize = text.fontSize;
        float height = rect.rect.height;
        // A degenerate rect would collapse auto-sizing straight to the floor.
        // Skip without recording state so a later scan can retry.
        if (baseSize <= 1f || height < baseSize * 1.5f)
            return;

        TmpFitState state = new TmpFitState();
        state.OriginalAutoSizing = text.enableAutoSizing;
        state.OriginalMin = text.fontSizeMin;
        state.OriginalMax = text.fontSizeMax;
        state.OriginalSize = baseSize;
        ConfigureTmp(text, state, baseSize);
        tmpStates.Add(text, state);

        if (logger != null)
            logger.LogInfo(
                "Dialogue fit attached: " + DescribePath(text.transform) +
                " base=" + baseSize.ToString("0.#") +
                " rect=" + rect.rect.width.ToString("0") + "x" + height.ToString("0") +
                " range=" + state.AppliedMin.ToString("0.#") +
                "-" + state.AppliedMax.ToString("0.#"));
    }

    private void ConfigureTmp(
        Naninovel.UI.RevealableTMProText text,
        TmpFitState state,
        float baseSize)
    {
        state.AppliedMax = baseSize;
        state.AppliedMin = baseSize * minScale;
        state.LastObservedSize = baseSize;
        text.fontSizeMax = state.AppliedMax;
        text.fontSizeMin = state.AppliedMin;
        text.enableAutoSizing = true;
    }

    private void TrackUgui(Naninovel.UI.RevealableUIText text)
    {
        if (text == null)
            return;

        UguiFitState known;
        if (uguiStates.TryGetValue(text, out known))
        {
            known.LastObservedSize = text.fontSize;
            return;
        }

        if (!text.gameObject.scene.IsValid())
            return;

        RectTransform rect = text.rectTransform;
        if (rect == null)
            return;

        int baseSize = text.fontSize;
        float height = rect.rect.height;
        if (baseSize <= 1 || height < baseSize * 1.5f)
            return;

        UguiFitState state = new UguiFitState();
        state.OriginalBestFit = text.resizeTextForBestFit;
        state.OriginalMin = text.resizeTextMinSize;
        state.OriginalMax = text.resizeTextMaxSize;
        ConfigureUgui(text, state, baseSize);
        uguiStates.Add(text, state);

        if (logger != null)
            logger.LogInfo(
                "Dialogue fit attached: " + DescribePath(text.transform) +
                " base=" + baseSize +
                " rect=" + rect.rect.width.ToString("0") + "x" + height.ToString("0") +
                " range=" + state.AppliedMin + "-" + state.AppliedMax);
    }

    private void ConfigureUgui(
        Naninovel.UI.RevealableUIText text,
        UguiFitState state,
        int baseSize)
    {
        int floor = Mathf.RoundToInt(baseSize * minScale);
        if (floor < 1)
            floor = 1;
        state.AppliedMax = baseSize;
        state.AppliedMin = floor;
        state.LastObservedSize = baseSize;
        text.resizeTextMaxSize = state.AppliedMax;
        text.resizeTextMinSize = state.AppliedMin;
        text.resizeTextForBestFit = true;
    }

    // Naninovel applies the player's font size option through CustomUI.SetFontSize.
    // Re-pin the authored maximum to whatever the engine just requested, so the
    // in-game text size setting keeps working while Korean mode is on.
    internal void Rebaseline(Naninovel.UI.CustomUI ui)
    {
        if (ui == null)
            return;

        Naninovel.UI.RevealableTMProText[] tmpTexts =
            ui.GetComponentsInChildren<Naninovel.UI.RevealableTMProText>(true);
        for (int index = 0; index < tmpTexts.Length; index++)
        {
            Naninovel.UI.RevealableTMProText text = tmpTexts[index];
            TmpFitState state;
            if (text == null || !tmpStates.TryGetValue(text, out state))
                continue;
            float requested = text.fontSize;
            // Unchanged means this component is not driven by the option; the
            // value we see is only what auto-sizing produced.
            if (Mathf.Approximately(requested, state.LastObservedSize))
                continue;
            if (requested <= 1f)
                continue;
            ConfigureTmp(text, state, requested);
        }

        Naninovel.UI.RevealableUIText[] uguiTexts =
            ui.GetComponentsInChildren<Naninovel.UI.RevealableUIText>(true);
        for (int index = 0; index < uguiTexts.Length; index++)
        {
            Naninovel.UI.RevealableUIText text = uguiTexts[index];
            UguiFitState state;
            if (text == null || !uguiStates.TryGetValue(text, out state))
                continue;
            int requested = text.fontSize;
            if (requested == state.LastObservedSize || requested <= 1)
                continue;
            ConfigureUgui(text, state, requested);
        }
    }

    internal void Restore()
    {
        foreach (KeyValuePair<Naninovel.UI.RevealableTMProText, TmpFitState> pair in tmpStates)
        {
            Naninovel.UI.RevealableTMProText text = pair.Key;
            if (text == null)
                continue;
            TmpFitState state = pair.Value;
            text.enableAutoSizing = state.OriginalAutoSizing;
            text.fontSizeMin = state.OriginalMin;
            text.fontSizeMax = state.OriginalMax;
            text.fontSize = state.OriginalSize;
        }
        tmpStates.Clear();

        foreach (KeyValuePair<Naninovel.UI.RevealableUIText, UguiFitState> pair in uguiStates)
        {
            Naninovel.UI.RevealableUIText text = pair.Key;
            if (text == null)
                continue;
            UguiFitState state = pair.Value;
            text.resizeTextForBestFit = state.OriginalBestFit;
            text.resizeTextMinSize = state.OriginalMin;
            text.resizeTextMaxSize = state.OriginalMax;
        }
        uguiStates.Clear();
    }

    private static string DescribePath(Transform transform)
    {
        if (transform == null)
            return "<null>";

        string path = transform.name;
        Transform current = transform.parent;
        for (int depth = 0; depth < 3 && current != null; depth++)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }
        return path;
    }
}

// Reports, once per component, what a visible Korean text is actually rendering
// with: its font asset, whether the Korean fallback is attached, which glyphs
// the font chain cannot supply, and the atlas texture behind every sub-mesh.
// Text that shows up as empty boxes leaves a Warning line naming the object.
internal sealed class TextDiagnostics
{
    private const int InfoBudget = 300;
    private const int WarningBudget = 400;
    private const int SeenLimit = 4000;

    private readonly ManualLogSource logger;
    // The reported symptom is intermittent, so a component that once rendered
    // correctly must be re-examined whenever its text, font, fallback, atlas,
    // TMP sub-mesh material or Naninovel reveal-material cache changes.
    private readonly Dictionary<int, string> lastSeen = new Dictionary<int, string>();
    private readonly HashSet<string> reportedFailures =
        new HashSet<string>(StringComparer.Ordinal);
    private int infoLeft = InfoBudget;
    private int warningLeft = WarningBudget;

    internal TextDiagnostics(ManualLogSource log)
    {
        logger = log;
    }

    internal void Scan(bool active, KoreanFontBridge fonts)
    {
        // Warnings keep their own budget so routine Info lines can never crowd
        // out the failure this log exists to capture.
        if (!active || fonts == null || warningLeft <= 0)
            return;
        if (lastSeen.Count > SeenLimit)
            lastSeen.Clear();

        TMP_FontAsset koreanTmp = fonts.KoreanTmpFont;
        TMP_Text[] tmpTexts = Resources.FindObjectsOfTypeAll<TMP_Text>();
        for (int index = 0; index < tmpTexts.Length; index++)
            InspectTmp(tmpTexts[index], koreanTmp);

        Font koreanUgui = fonts.KoreanUguiFont;
        Text[] uguiTexts = Resources.FindObjectsOfTypeAll<Text>();
        for (int index = 0; index < uguiTexts.Length; index++)
            InspectUgui(uguiTexts[index], koreanUgui);
    }

    // Returns false only when every rendering-relevant value represented by the
    // caller-provided signature is unchanged.
    private bool Changed(int id, string signature)
    {
        string previous;
        if (lastSeen.TryGetValue(id, out previous) && previous == signature)
            return false;
        lastSeen[id] = signature;
        return true;
    }

    private void Report(int id, string missing, string line)
    {
        if (missing.Length > 0)
        {
            if (!reportedFailures.Add(id + ":" + missing) || warningLeft <= 0)
                return;
            warningLeft--;
            logger.LogWarning(line);
            return;
        }
        if (infoLeft <= 0)
            return;
        infoLeft--;
        logger.LogInfo(line);
    }

    private void InspectTmp(TMP_Text text, TMP_FontAsset koreanTmp)
    {
        if (!Eligible(text, text == null ? null : text.text))
            return;

        TMP_FontAsset font = text.font;
        bool fallback = font != null && koreanTmp != null &&
            font.fallbackFontAssetTable != null &&
            font.fallbackFontAssetTable.Contains(koreanTmp);
        bool cacheMismatch;
        string renderState = DescribeTmpRenderState(text, out cacheMismatch);
        string signature =
            (font == null
                ? "-"
                : font.name + "#" + font.GetInstanceID()) +
            "" + fallback +
            "" + (koreanTmp == null ? 0 : koreanTmp.atlasTextureCount) +
            "" + renderState +
            "" + text.text;
        if (!Changed(text.GetInstanceID(), signature))
            return;

        string missing = MissingTmpGlyphs(font, text.text);

        string line =
            "[glyph] TMP " + DescribePath(text.transform) +
            " font=" + (font == null ? "<null>" : font.name) +
            " koFallback=" + fallback +
            " koPages=" + (koreanTmp == null ? 0 : koreanTmp.atlasTextureCount) +
            " " + renderState +
            " missing=" + (missing.Length == 0 ? "-" : missing) +
            " text=" + Preview(text.text);

        string failure =
            missing.Length > 0
                ? "glyph:" + missing
                : (cacheMismatch ? "reveal-material-cache" : String.Empty);
        Report(text.GetInstanceID(), failure, line);
    }

    private void InspectUgui(Text text, Font koreanUgui)
    {
        if (!Eligible(text, text == null ? null : text.text))
            return;

        Font font = text.font;
        string signature =
            (font == null
                ? "-"
                : font.name + "#" + font.GetInstanceID()) +
            "" + (font != null && font == koreanUgui) +
            "" + (font != null && font.dynamic) +
            "" + text.text;
        if (!Changed(text.GetInstanceID(), signature))
            return;

        string missing = MissingUguiGlyphs(font, text.text);

        string line =
            "[glyph] UGUI " + DescribePath(text.transform) +
            " font=" + (font == null ? "<null>" : font.name) +
            " isKoreanFont=" + (font != null && font == koreanUgui) +
            " dynamic=" + (font != null && font.dynamic) +
            " missing=" + (missing.Length == 0 ? "-" : missing) +
            " text=" + Preview(text.text);

        Report(text.GetInstanceID(), missing, line);
    }

    private static bool Eligible(Component component, string value)
    {
        if (component == null || String.IsNullOrEmpty(value))
            return false;
        // Resources.FindObjectsOfTypeAll also returns prefab assets and hidden
        // objects; only report what a player can actually be looking at.
        if (!component.gameObject.scene.IsValid())
            return false;
        Behaviour behaviour = component as Behaviour;
        if (behaviour != null && !behaviour.isActiveAndEnabled)
            return false;
        return HasHangul(value);
    }

    private static bool HasHangul(string value)
    {
        for (int index = 0; index < value.Length; index++)
            if (IsHangul(value[index]))
                return true;
        return false;
    }

    private static bool IsHangul(char character)
    {
        return (character >= 'ᄀ' && character <= 'ᇿ') ||
            (character >= '㄰' && character <= '㆏') ||
            (character >= '가' && character <= '힣');
    }

    private static string MissingTmpGlyphs(TMP_FontAsset font, string value)
    {
        if (font == null)
            return "<no font>";
        StringBuilder builder = new StringBuilder();
        HashSet<char> seen = new HashSet<char>();
        for (int index = 0; index < value.Length && builder.Length < 12; index++)
        {
            char character = value[index];
            if (!IsHangul(character) || !seen.Add(character))
                continue;
            bool present;
            try
            {
                present = font.HasCharacter(character, true, false);
            }
            catch (Exception)
            {
                present = true;
            }
            if (!present)
                builder.Append(character);
        }
        return builder.ToString();
    }

    private static string MissingUguiGlyphs(Font font, string value)
    {
        if (font == null)
            return "<no font>";
        StringBuilder builder = new StringBuilder();
        HashSet<char> seen = new HashSet<char>();
        for (int index = 0; index < value.Length && builder.Length < 12; index++)
        {
            char character = value[index];
            if (!IsHangul(character) || !seen.Add(character))
                continue;
            bool present;
            try
            {
                present = font.HasCharacter(character);
            }
            catch (Exception)
            {
                present = true;
            }
            if (!present)
                builder.Append(character);
        }
        return builder.ToString();
    }

    // Korean glyphs arrive through a fallback sub-mesh. Record object identity,
    // not just names and dimensions: two material arrays can look identical in
    // a terse log while one still references an obsolete atlas/material object.
    private static string DescribeTmpRenderState(
        TMP_Text text,
        out bool cacheMismatch)
    {
        cacheMismatch = false;
        try
        {
            TMP_TextInfo info = text.textInfo;
            if (info == null)
                return "materials=?";

            Material[] shared = text.fontSharedMaterials;
            StringBuilder builder = new StringBuilder();
            builder.Append("materials=").Append(info.materialCount);
            AppendMaterials(builder, "shared", shared, info.materialCount);

            int meshLimit = info.meshInfo == null ? 0 : info.meshInfo.Length;
            builder.Append(" mesh=").Append(meshLimit);
            int reportedMesh = Math.Min(info.materialCount, meshLimit);
            for (int index = 0; index < reportedMesh && index < 8; index++)
            {
                Material material = info.meshInfo[index].material;
                builder.Append('|').Append(DescribeMaterial(material));
            }

            Naninovel.UI.RevealableTMProText revealable =
                text as Naninovel.UI.RevealableTMProText;
            if (revealable != null)
            {
                Material[] cached =
                    RevealableTmpMaterialRepair.Read(revealable);
                AppendMaterials(builder, "revealCache", cached, -1);
                if (cached != null)
                {
                    int expectedCount = info.materialCount;
                    cacheMismatch =
                        shared == null ||
                        cached.Length != expectedCount ||
                        shared.Length < expectedCount;
                    if (!cacheMismatch)
                    {
                        for (int index = 0; index < expectedCount; index++)
                        {
                            if (cached[index] != shared[index])
                            {
                                cacheMismatch = true;
                                break;
                            }
                        }
                    }
                }
                builder.Append(" cacheMatch=")
                    .Append(cached == null ? "uninitialized" :
                        (cacheMismatch ? "false" : "true"));
            }
            return builder.ToString();
        }
        catch (Exception)
        {
            return "materials=<error>";
        }
    }

    private static void AppendMaterials(
        StringBuilder builder,
        string label,
        Material[] materials,
        int logicalCount)
    {
        builder.Append(' ').Append(label).Append('=');
        if (materials == null)
        {
            builder.Append("<null>");
            return;
        }

        builder.Append(materials.Length);
        int count = logicalCount < 0
            ? materials.Length
            : Math.Min(logicalCount, materials.Length);
        for (int index = 0; index < count && index < 8; index++)
            builder.Append('|').Append(DescribeMaterial(materials[index]));
    }

    private static string DescribeMaterial(Material material)
    {
        if (material == null)
            return "<null>";
        Texture atlas = material.mainTexture;
        StringBuilder builder = new StringBuilder();
        builder.Append(material.name)
            .Append('#').Append(material.GetInstanceID());
        if (atlas == null)
            return builder.Append("/<noTex>").ToString();
        return builder.Append('/')
            .Append(atlas.name).Append('#').Append(atlas.GetInstanceID())
            .Append('(').Append(atlas.width)
            .Append('x').Append(atlas.height).Append(')')
            .ToString();
    }

    private static string Preview(string value)
    {
        string flat = value.Replace("\n", " ").Replace("\r", " ");
        if (flat.Length > 34)
            flat = flat.Substring(0, 34) + "…";
        return flat;
    }

    private static string DescribePath(Transform transform)
    {
        if (transform == null)
            return "<null>";
        string path = transform.name;
        Transform current = transform.parent;
        for (int depth = 0; depth < 4 && current != null; depth++)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }
        return path;
    }
}

[HarmonyPatch(typeof(Naninovel.UI.CustomUI), "SetFontSize")]
internal static class PrinterFontSizePatch
{
    [HarmonyPostfix]
    private static void Postfix(Naninovel.UI.CustomUI __instance)
    {
        LightKravteKorean plugin = LightKravteKorean.Instance;
        if (plugin == null || !plugin.KoreanActive || plugin.TextFit == null)
            return;
        plugin.TextFit.Rebaseline(__instance);
    }
}
