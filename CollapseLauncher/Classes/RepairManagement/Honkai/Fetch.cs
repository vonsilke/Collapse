﻿using Hi3Helper;
using Hi3Helper.EncTool;
using Hi3Helper.EncTool.KianaManifest;
using Hi3Helper.Http;
using Hi3Helper.Shared.ClassStruct;
using Hi3Helper.Shared.Region.Honkai;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static Hi3Helper.Data.ConverterTool;
using static Hi3Helper.Locale;
using static Hi3Helper.Logger;
using static Hi3Helper.Preset.ConfigV2Store;
using static Hi3Helper.Shared.Region.LauncherConfig;

namespace CollapseLauncher
{
    internal partial class HonkaiRepair
    {
        private async Task Fetch(List<FilePropertiesRemote> assetIndex, CancellationToken token)
        {
            // Set total activity string as "Loading Indexes..."
            _status.ActivityStatus = Lang._GameRepairPage.Status2;
            _status.IsProgressTotalIndetermined = true;
            UpdateStatus();

            // Use HttpClient instance on fetching
            Http _httpClient = new Http(true, 5, 1000, _userAgent);
            try
            {
                // Subscribe the fetching progress
                _httpClient.DownloadProgress += _httpClient_FetchAssetProgress;

                // Region: XMFAndAssetIndex
                // Fetch metadata
                Dictionary<string, string> manifestDict = await FetchMetadata(_httpClient, token);

                // Check for manifest. If it doesn't exist, then throw and warn the user
                if (!manifestDict.ContainsKey(_gameVersion.VersionString))
                {
                    throw new VersionNotFoundException($"Manifest for {_gamePreset.ZoneName} (version: {_gameVersion.VersionString}) doesn't exist! Please contact @neon-nyan or open an issue for this!");
                }

                // Region: XMFAndAssetIndex
                // Fetch asset index
                await FetchAssetIndex(_httpClient, assetIndex, token);

                // Region: XMFAndAssetIndex
                // Try check XMF file and fetch it if it doesn't exist
                await FetchXMFFile(_httpClient, manifestDict[_gameVersion.VersionString], token);

                // Region: AudioIndex
                // Try check audio manifest.m file and fetch it if it doesn't exist
                await FetchAudioIndex(_httpClient, assetIndex, token);
            }
            catch { throw; }
            finally
            {
                // Unsubscribe the fetching progress and dispose it
                _httpClient.DownloadProgress -= _httpClient_FetchAssetProgress;
                _httpClient.Dispose();
            }
        }

        #region AudioIndex
        private async Task FetchAudioIndex(Http _httpClient, List<FilePropertiesRemote> assetIndex, CancellationToken token)
        {
            // Assign Base Asset URL
            _assetBaseURL = await GetAssetBaseURL(_httpClient, token);

            // Set manifest.m local path and remote URL
            string manifestLocalPath = Path.Combine(_gamePath, NormalizePath(_audioBaseLocalPath), "manifest.m");
            string manifestRemotePath = string.Format(_audioBaseRemotePath + "manifest.m", $"{_gameVersion.Major}_{_gameVersion.Minor}");
            Manifest manifest;

            try
            {
                // Try to get the audio manifest and deserialize it
                manifest = await TryGetAudioManifest(_httpClient, manifestLocalPath, manifestRemotePath, token);

                // Deserialize manifest and build Audio Index
                await BuildAudioIndex(_httpClient, manifest, assetIndex, token);

                // Build audio version file
                BuildAudioVersioningFile(manifest);
            }
            // If a throw was thrown, then try to redownload the manifest.m file and try deserialize it again
            catch (Exception ex)
            {
                LogWriteLine($"Exception was thrown while reading audio manifest file!\r\n{ex}", LogType.Warning, true);
                return;
            }
        }

        private void BuildAudioVersioningFile(Manifest audioManifest)
        {
            // Build audio versioning file
            using (StreamWriter sw = new StreamWriter(Path.Combine(_gamePath, NormalizePath(_audioBaseLocalPath), "Version.txt"), false))
            {
                foreach (ManifestAssetInfo audioAsset in audioManifest.AudioAssets.Where(x => x.Language == _audioLanguage || x.Language == AssetLanguage.Common))
                {
                    sw.WriteLine($"{audioAsset.Name}.pck\t{audioAsset.HashString}");
                }
            }
        }

        private void CountAudioIndex(List<FilePropertiesRemote> audioIndex)
        {
            // Increment the total count and total size
            List<FilePropertiesRemote> assets = audioIndex.Where(x => x.FT == FileType.Audio).ToList();
            _progressTotalCount += assets.Count;
            _progressTotalSize += assets.Sum(x => x.S);
        }

        private async Task BuildAudioIndex(Http _httpClient, Manifest audioManifest, List<FilePropertiesRemote> audioIndex, CancellationToken token)
        {
            // Iterate the audioAsset to be added in audioIndex
            foreach (ManifestAssetInfo audioInfo in audioManifest.AudioAssets)
            {
                // Only add common and language specific audio file
                bool isAudioFilePersistent = IsAudioFilePersistent(audioInfo);
                if (audioInfo.Language == AssetLanguage.Common || audioInfo.Language == _audioLanguage || isAudioFilePersistent)
                {
                    // Try get the availability of the audio asset
                    if (await IsAudioFileAvailable(_httpClient, audioInfo, token))
                    {
                        // Assign based on each values
                        FilePropertiesRemote audioAsset = new FilePropertiesRemote
                        {
                            RN = audioInfo.Path,
                            N = _audioBaseLocalPath + audioInfo.Name + ".pck",
                            S = audioInfo.Size,
                            FT = FileType.Audio,
                            CRC = audioInfo.HashString,
                            AudioPatchInfo = audioInfo.IsHasPatch ? audioInfo.PatchInfo : null,
                        };

                        // Add audioAsset to audioIndex
                        audioIndex.Add(audioAsset);
                    }
                }
            }
        }

        private bool IsAudioFilePersistent(ManifestAssetInfo audioInfo) => _audioPersistentAssets.Any(audioInfo.Name.Contains);

        private async ValueTask<bool> IsAudioFileAvailable(Http _httpClient, ManifestAssetInfo audioInfo, CancellationToken token)
        {
            // If the file is static (NeedMap == true), then pass
            if (audioInfo.NeedMap) return true;

            // Update the status
            _status.ActivityStatus = string.Format("Trying to determine audio asset availability: {0}", audioInfo.Path);
            _status.IsProgressTotalIndetermined = true;
            _status.IsProgressPerFileIndetermined = true;
            UpdateStatus();

            // Set the URL and try get the status
            string audioURL = string.Format(_audioBaseRemotePath, $"{_gameVersion.Major}_{_gameVersion.Minor}") + audioInfo.Path;
            (int, bool) urlStatus = await _httpClient.GetURLStatus(audioURL, token);

            LogWriteLine($"The audio asset: {audioInfo.Path} " + (urlStatus.Item2 ? "is" : "is not") + $" available (Status code: {urlStatus.Item1})", LogType.Default, true);

            return urlStatus.Item2;
        }

        private string GetXmlConfigKey()
        {
            // Initialize keyTool
            mhyEncTool keyTool = new mhyEncTool();
            keyTool.InitMasterKey(ConfigV2.MasterKey, ConfigV2.MasterKeyBitLength, RSAEncryptionPadding.Pkcs1);

            // Return the key
            return keyTool.GetMasterKey();
        }

        private string GetPreferredGatewayURL(Dispatcher dispatcher, string preferredGateway)
        {
            // If preferredGateway == null, then return the first dispatch_url
            if (preferredGateway == null)
            {
                return BuildGatewayURL(dispatcher.region_list[0].dispatch_url);
            }

            // Find the preferred region_list and return the dispatcher_url if found or null if doesn't
            return BuildGatewayURL(dispatcher.region_list.Where(x => x.name == preferredGateway).FirstOrDefault().dispatch_url);
        }

        private async Task<string> GetAssetBaseURL(Http _httpClient, CancellationToken token)
        {
            // Fetch dispatcher
            Dispatcher dispatcher = null;
            Exception lastException = null;

            // Try fetch disppatcher by iterating the base URL
            foreach (string baseURL in _gamePreset.GameDispatchArrayURL)
            {
                try
                {
                    // Try assign dispatcher
                    dispatcher = await FetchDispatcher(_httpClient, BuildDispatcherURL(baseURL), token);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                // If no exception being thrown, then break
                if (lastException == null)
                {
                    break;
                }
            }

            // If last exception wasn't null, then throw
            if (lastException != null) throw lastException;

            // Get gatewayURl and fetch the gateway
            string gatewayURL = GetPreferredGatewayURL(dispatcher, _gamePreset.GameGatewayDefault);
            Gateway gateway = await FetchGateway(_httpClient, gatewayURL, token);

            // Return the first external resource list URL
            return $"http://{gateway.ex_resource_url_list[0]}/";
        }

        private async Task<Manifest> TryGetAudioManifest(Http _httpClient, string manifestLocal, string manifestRemote, CancellationToken token)
        {
            // Always check if the folder is exist
            string manifestFolder = Path.GetDirectoryName(manifestLocal);
            if (!Directory.Exists(manifestFolder))
            {
                Directory.CreateDirectory(manifestFolder);
            }

            // Start downloading manifest.m
            await _httpClient.Download(manifestRemote, manifestLocal, true, null, null, token);

            // Get the XML key and deserialize the manifest
            string xmlKey = GetXmlConfigKey();
            return new Manifest(manifestLocal, xmlKey, _gameVersion.VersionArrayAudioManifest);
        }

        private async Task<Dispatcher> FetchDispatcher(Http _httpClient, string baseURL, CancellationToken token)
        {
            // Get the dispatcher properties
            MemoryStream stream = new MemoryStream();

            try
            {
                // Start downloading the dispatcher
                await _httpClient.Download(baseURL, stream, null, null, token);
                stream.Position = 0;

                LogWriteLine($"Cache Update connected to dispatcher endpoint: {baseURL}", LogType.Default, true);

                // Try deserialize dispatcher
                return (Dispatcher)JsonSerializer.Deserialize(stream, typeof(Dispatcher), DispatcherContext.Default);
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                // Dispose the stream
                stream.Dispose();
            }
        }

        private async Task<Gateway> FetchGateway(Http _httpClient, string baseURL, CancellationToken token)
        {
            // Get the gateway properties
            MemoryStream stream = new MemoryStream();

            try
            {
                // Start downloading the gateway
                await _httpClient.Download(baseURL, stream, null, null, token);
                stream.Position = 0;

                LogWriteLine($"Cache Update connected to gateway endpoint: {baseURL}", LogType.Default, true);

                // Try deserialize gateway
                return (Gateway)JsonSerializer.Deserialize(stream, typeof(Gateway), GatewayContext.Default);
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                // Unsubscribe progress and dispose stream
                stream.Dispose();
            }
        }

        private string BuildDispatcherURL(string baseDispatcherURL)
        {
            // Format the Dispatcher URL based on template
            long curTime = (int)Math.Truncate(DateTime.Now.Subtract(new DateTime(1970, 1, 1)).TotalSeconds);
            return string.Format($"{baseDispatcherURL}{_gamePreset.GameDispatchURLTemplate}", _gameVersion.VersionString, _gamePreset.GameDispatchChannelName, curTime);
        }

        private string BuildGatewayURL(string baseGatewayURL)
        {
            // Format the Gateway URL based on template
            long curTime = (int)Math.Truncate(DateTime.Now.Subtract(new DateTime(1970, 1, 1)).TotalSeconds);
            return string.Format($"{baseGatewayURL}{_gamePreset.GameGatewayURLTemplate}", _gameVersion.VersionString, _gamePreset.GameDispatchChannelName, curTime);
        }
        #endregion

        #region XMFAndAssetIndex
        private async Task<Dictionary<string, string>> FetchMetadata(Http _httpClient, CancellationToken token)
        {
            // Fetch metadata dictionary
            using (MemoryStream mfs = new MemoryStream())
            {
                // Set metadata URL
                string urlMetadata = string.Format(AppGameRepoIndexURLPrefix, _gamePreset.ProfileName);

                // Start downloading metadata
                await FallbackCDNUtil.DownloadCDNFallbackContent(_httpClient, mfs, urlMetadata, token);

                // Deserialize metadata
                mfs.Position = 0;
                return (Dictionary<string, string>)JsonSerializer.Deserialize(mfs, typeof(Dictionary<string, string>), D_StringString.Default);
            }
        }

        private async Task FetchAssetIndex(Http _httpClient, List<FilePropertiesRemote> assetIndex, CancellationToken token)
        {
            try
            {
                // Use FallbackCDNUtil for fetching progress
                FallbackCDNUtil.DownloadProgress += _httpClient_FetchAssetProgress;
                // Fetch asset index
                using (MemoryStream mfs = new MemoryStream())
                {
                    // Set asset index URL
                    string urlIndex = string.Format(AppGameRepairIndexURLPrefix, _gamePreset.ProfileName, _gameVersion.VersionString);

                    // Start downloading asset index using FallbackCDNUtil
                    await FallbackCDNUtil.DownloadCDNFallbackContent(_httpClient, mfs, urlIndex, token);

                    // Deserialize asset index and return
                    mfs.Position = 0;
                    assetIndex.AddRange((List<FilePropertiesRemote>)JsonSerializer.Deserialize(mfs, typeof(List<FilePropertiesRemote>), L_FilePropertiesRemoteContext.Default));
                }
            }
            catch { throw; }
            finally
            {
                // Unsubscribe FallbackCDNUtil progress
                FallbackCDNUtil.DownloadProgress += _httpClient_FetchAssetProgress;
            }
        }

        private async Task FetchXMFFile(Http _httpClient, string _repoURL, CancellationToken token)
        {
            // Set XMF Path and check if the XMF state is valid
            string xmfPath = Path.Combine(_gamePath, "BH3_Data\\StreamingAssets\\Asb\\pc\\Blocks.xmf");
            if (XMFUtility.CheckIfXMFVersionMatches(xmfPath, _gameVersion.VersionArrayXMF)) return;

            // Set XMF URL
            string urlXMF = _repoURL + '/' + _blockBasePath + "Blocks.xmf";

            // Start downloading XMF
            await _httpClient.Download(urlXMF, xmfPath, true, null, null, token);
        }

        private void CountAssetIndex(List<FilePropertiesRemote> assetIndex)
        {
            // Sum total size
            long blockSize = assetIndex
                .Where(x => x.BlkC != null)
                .Sum(x => x.BlkC
                    .Sum(y => y.BlockSize));
            _progressTotalSize = assetIndex.Where(x => x.FT != FileType.Audio).Sum(x => x.S) + blockSize;

            // Sum total count by adding AssetIndex.Count + Counts from assets with "Blocks" type.
            _progressTotalCount = assetIndex.Count + assetIndex.Where(x => x.BlkC != null && x.FT != FileType.Audio).Sum(y => y.BlkC.Sum(z => z.BlockContent.Count));
        }
        #endregion
    }
}
