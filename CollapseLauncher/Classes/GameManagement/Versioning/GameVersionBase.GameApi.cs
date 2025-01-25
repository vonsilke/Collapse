using Hi3Helper.Data;
using Hi3Helper.Shared.ClassStruct;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

// ReSharper disable IdentifierTypo
// ReSharper disable InconsistentNaming
// ReSharper disable StringLiteralTypo
// ReSharper disable CheckNamespace

#nullable enable
namespace CollapseLauncher.GameManagement.Versioning
{
    internal partial class GameVersionBase
    {
        #region Game Region Resource Prop
        public RegionResourceProp GameApiProp { get; set; }

        // Assign for the Game Delta-Patch properties (if any).
        // If there's no Delta-Patch, then set it to null.
        protected virtual DeltaPatchProperty? GameDeltaPatchProp
        {
            get
            {
                if (string.IsNullOrEmpty(GamePreset.ProfileName))
                {
                    throw new NullReferenceException("Cannot get delta patch property as GamePreset -> ProfileName is null or empty!");
                }
                return CheckDeltaPatchUpdate(GameDirPath, GamePreset.ProfileName, GameVersionAPI ?? throw new NullReferenceException("GameVersionAPI returns a null"));
            }
        }

        protected virtual List<RegionResourcePlugin>? MismatchPlugin { get; set; }
        #endregion

        #region Game Version API Properties
        protected virtual GameVersion? SdkVersionAPI
        {
            get
            {
                // Return null if the plugin is not exist
                if (GameApiProp.data?.sdk == null)
                    return null;

                // If the version provided by the SDK API, return the result
                return GameVersion.TryParse(GameApiProp.data?.sdk?.version, out GameVersion? result) ? result :
                    // Otherwise, return null
                    null;
            }
        }

        protected virtual GameVersion? GameVersionAPI
        {
            get => field ??= GameVersion.TryParse(GameApiProp.data?.game?.latest?.version, out GameVersion? version) ? version : null;
        }

        protected virtual GameVersion? GameVersionAPIPreload
        {
            get
            {
                if (field != null)
                {
                    return field;
                }

                GameVersion? currentInstalled = GameVersionInstalled;

                // If no installation installed, then return null
                if (currentInstalled == null)
                    return null;

                // Check if the pre_download_game property has value. If not, then return null
                if (string.IsNullOrEmpty(GameApiProp.data?.pre_download_game?.latest?.version))
                    return null;

                return field = new GameVersion(GameApiProp.data.pre_download_game.latest.version);
            }
        }

        [field: AllowNull, MaybeNull]
        protected virtual Dictionary<string, GameVersion> PluginVersionsAPI
        {
            get
            {
                // If field is not null, then return the cached dictionary
                if (field != null)
                {
                    return field;
                }

                // Initialize dictionary
                Dictionary<string, GameVersion> value = new();

                // Return empty if the plugin is not exist
                if (GameApiProp.data?.plugins == null || GameApiProp.data.plugins.Count == 0)
                {
                    return value;
                }

                // Get the version and convert it into GameVersion
                foreach (var plugin in GameApiProp.data.plugins
                                                  .Where(plugin => plugin.plugin_id != null)
                                                  .Select(plugin => (plugin.plugin_id, plugin.version)))
                {
                    if (string.IsNullOrEmpty(plugin.plugin_id))
                    {
                        continue;
                    }
                    value.TryAdd(plugin.plugin_id, new GameVersion(plugin.version));
                }

                return field = value;
            }
        }

        protected virtual GameVersion? GameVersionInstalled
        {
            get
            {
                // Check if the INI has game_version key...
                if (!GameIniVersion[DefaultIniVersionSection].TryGetValue("game_version", out IniValue gameVersion))
                {
                    return null;
                }

                // Get the game version
                string? val = gameVersion;

                // If not, then return as null
                if (string.IsNullOrEmpty(val))
                {
                    return null;
                }

                // Otherwise, return the game version
                return new GameVersion(val);
            }
            set
            {
                UpdateGameVersion(value ?? GameVersionAPI, false);
                UpdateGameChannels(false);
            }
        }

        protected virtual Dictionary<string, GameVersion>? PluginVersionsInstalled
        {
            get
            {
                // If field is not null, then return the cached dictionary
                if (field != null)
                {
                    return field;
                }

                // Initialize dictionary
                Dictionary<string, GameVersion> value = new();

                // Return empty if the plugin is not exist
                if (GameApiProp.data?.plugins == null || GameApiProp.data?.plugins?.Count == 0)
                {
                    return value;
                }

                // Get the version and convert it into GameVersion
                foreach (var plugin in GameApiProp.data?.plugins!)
                {
                    // Check if the INI has plugin_ID_version key...
                    string keyName = $"plugin_{plugin.plugin_id}_version";
                    if (!GameIniVersion[DefaultIniVersionSection].TryGetValue(keyName, out IniValue getPluginVersion))
                    {
                        continue;
                    }

                    string? val = getPluginVersion;
                    if (string.IsNullOrEmpty(val))
                    {
                        continue;
                    }

                    if (plugin.plugin_id != null)
                    {
                        _ = value.TryAdd(plugin.plugin_id, new GameVersion(val));
                    }
                }

                return field = value;
            }
            set => UpdatePluginVersions(field = value ?? PluginVersionsAPI);
        }

        protected virtual GameVersion? SdkVersionInstalled
        {
            get
            {
                // If it's already cached, then return
                if (field != null)
                {
                    return field;
                }

                // Check if the game config has SDK version. If not, return null
                const string keyName = "plugin_sdk_version";
                if (!GameIniVersion[DefaultIniVersionSection].TryGetValue(keyName, out IniValue pluginSdkVersion))
                    return null;

                // Check if it has no value, then return null
                string? versionName = pluginSdkVersion;
                if (string.IsNullOrEmpty(versionName))
                    return null;

                // Try parse the version.
                return field = !GameVersion.TryParse(versionName, out GameVersion? result) ?
                    // If it's not valid, then return null
                    null :
                    // Otherwise, return the result
                    result;
            }
            set => UpdateSdkVersion(field = value ?? SdkVersionAPI);
        }
        #endregion

        #region Game Version API Methods
        public GameVersion? GetGameExistingVersion() => GameVersionInstalled;

        public GameVersion? GetGameVersionApi() => GameVersionAPI;

        public GameVersion? GetGameVersionApiPreload() => GameVersionAPIPreload;

        
        #endregion

        #region Game Info Methods
        public virtual DeltaPatchProperty? GetDeltaPatchInfo() => GameDeltaPatchProp;

        public async ValueTask<GameInstallStateEnum> GetGameState()
        {
            // Check if the game installed first
            // If the game is installed, then move to another step.
            if (!IsGameInstalled())
            {
                return GameInstallStateEnum.NotInstalled;
            }

            // If the game version is not match, return need update.
            // Otherwise, move to the next step.
            if (!IsGameVersionMatch())
            {
                return GameInstallStateEnum.NeedsUpdate;
            }

            // Check for the game/plugin version and preload availability.
            if (IsGameHasPreload())
            {
                return GameInstallStateEnum.InstalledHavePreload;
            }

            // If the plugin version is not match, return that it's installed but have plugin updates.
            if (!await IsPluginVersionsMatch() || !await IsSdkVersionsMatch())
            {
                return GameInstallStateEnum.InstalledHavePlugin;
            }

            // If all passes, then return as Installed.
            return GameInstallStateEnum.Installed;
        }

        public virtual List<RegionResourceVersion> GetGameLatestZip(GameInstallStateEnum gameState)
        {
            // Initialize the return list
            List<RegionResourceVersion> returnList                 = [];
            RegionResourceVersion?      currentLatestRegionPackage = GameApiProp.data?.game?.latest;

            // If the current latest region package is null, then throw
            if (currentLatestRegionPackage == null)
            {
                throw new NullReferenceException("GameApiProp.data.game.latest returns a null!");
            }

            // If the GameVersion is not installed, then return the latest one
            if (gameState is GameInstallStateEnum.NotInstalled or GameInstallStateEnum.GameBroken)
            {
                // Add the latest prop to the return list
                returnList.Add(currentLatestRegionPackage);

                return returnList;
            }

            // Try to get the diff file  by the first or default (null)
            if (GameApiProp.data?.game?.diffs == null)
            {
                return returnList;
            }

            RegionResourceVersion? diff = GameApiProp.data?.game?.diffs
                                                     .FirstOrDefault(x => x.version == GameVersionInstalled?.VersionString);

            // Return if the diff is null, then get the latest. If found, then return the diff one.
            returnList.Add(diff ?? currentLatestRegionPackage);

            return returnList;
        }

        public virtual List<RegionResourceVersion>? GetGamePreloadZip()
        {
            // Initialize the return list
            List<RegionResourceVersion> returnList = [];

            // If the preload is not exist, then return null
            if (GameApiProp.data?.pre_download_game == null
                || (GameApiProp.data?.pre_download_game?.diffs?.Count ?? 0) == 0
                && GameApiProp.data?.pre_download_game?.latest == null)
            {
                return null;
            }

            // Try to get the diff file  by the first or default (null)
            RegionResourceVersion? diff = GameApiProp.data?.pre_download_game?.diffs?
               .FirstOrDefault(x => x.version == GameVersionInstalled?.VersionString);

            // If the single entry of the diff is null, then return null
            // If the diff is null, then get the latest.
            // If diff is found, then add the diff one.
            returnList.Add(diff ?? GameApiProp.data?.pre_download_game?.latest ?? throw new NullReferenceException("Preload neither have diff or latest package!"));

            // Return the list
            return returnList;
        }

        public virtual List<RegionResourcePlugin>? GetGamePluginZip()
        {
            // Check if the plugin is not empty, then add it
            if ((GameApiProp?.data?.plugins?.Count ?? 0) != 0)
                return [.. GameApiProp?.data?.plugins!];

            // Return null if plugin is unavailable
            return null;
        }

        public virtual List<RegionResourcePlugin>? GetGameSdkZip()
        {
            // Check if the sdk is not empty, then add it
            if (GameApiProp?.data?.sdk == null)
            {
                return null;
            }

            // Convert the value
            RegionResourcePlugin sdkPlugin = new RegionResourcePlugin
            {
                plugin_id  = "sdk",
                release_id = "sdk",
                version    = GameApiProp?.data?.sdk.version,
                package    = GameApiProp?.data?.sdk
            };

            // If the package is not null, then add the validation
            if (sdkPlugin.package != null)
            {
                sdkPlugin.package.pkg_version = GameApiProp?.data?.sdk?.pkg_version;
            }

            // Return a single list
            return [sdkPlugin];

            // Return null if sdk is unavailable
        }
        #endregion
    }
}