using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using PluginSdk.Commands;
using PluginSdk.Config;
using Shared.Config;
using Shared.Logging;
using Shared.Patches;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;

// Define assembly version when compiled by Magnetar
#if !LOCAL_BUILD
using System.Reflection;

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ServerPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin, ICommonPlugin
{
    public const string PluginName = "Hangar";
    public static string DefaultStorageRoot => Path.Combine(MyFileSystem.UserDataPath, PluginName);

    public static Plugin Instance { get; private set; }

    public long Tick { get; private set; }

    public IPluginLogger Log => Logger;
    private static readonly IPluginLogger Logger = new SdkPluginLogger(PluginName);

    public IPluginConfig Config => _config;
    public PluginConfig PluginConfig => _config;
    public HangarStorage Storage { get; private set; }

    private PluginConfig _config;
    private string _configPath;
    private bool _failed;
    private bool _initialized;
    private static readonly string ConfigFileName = $"{PluginName}.cfg";

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
#if DEBUG
        // Allow the debugger some time to connect once the plugin assembly is loaded.
        Thread.Sleep(100);
#endif

        Instance = this;

        Log.Info("Loading");

        _configPath = Path.Combine(MyFileSystem.UserDataPath, ConfigFileName);
        _config = ConfigStorage.LoadXml<PluginConfig>(_configPath);

        var gameVersion = MyFinalBuildConstants.APP_VERSION_STRING.ToString();
        Common.SetPlugin(this, gameVersion, MyFileSystem.UserDataPath);

        if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(PluginName)))
        {
            _failed = true;
            return;
        }

        try
        {
            ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(HangarCommands), typeof(HangarShortCommands));
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to register Hangar chat commands.");
        }

        Storage = new HangarStorage(this);
        Storage.EnsureStorage();
        _config.PropertyChanged += ConfigChanged;

        _initialized = true;
        Log.Debug("Successfully loaded");
    }

    public void Dispose()
    {
        try
        {
            if (_initialized)
            {
                Log.Debug("Disposing");
                if (_config != null)
                {
                    _config.PropertyChanged -= ConfigChanged;
                    SaveConfig();
                }

                Log.Debug("Disposed");
            }
        }
        catch (Exception ex)
        {
            Log.Critical(ex, "Dispose failed");
        }

        Storage = null;
        Instance = null;
    }

    public void Update()
    {
        if (_failed)
            return;

#if DEBUG
        CustomUpdate();
        Tick++;
#else
        try
        {
            CustomUpdate();
            Tick++;
        }
        catch (Exception e)
        {
            Log.Critical(e, "Update failed");
            _failed = true;
        }
#endif
    }

    public void SetEnabled(bool enabled)
    {
        if (_config == null)
            return;

        _config.Enabled = enabled;
        SaveConfig();
    }

    public void SaveConfig()
    {
        if (_config == null || string.IsNullOrWhiteSpace(_configPath))
            return;

        try
        {
            ConfigStorage.SaveXml(_config, _configPath);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to save Hangar configuration.");
        }
    }

    private void CustomUpdate()
    {
        if (!_config.Enabled)
            return;

        PatchHelpers.PatchUpdates();
    }

    private void ConfigChanged(object sender, PropertyChangedEventArgs e)
    {
        SaveConfig();
    }
}
