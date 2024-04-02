﻿using Hi3Helper;
using Hi3Helper.Data;
using Hi3Helper.Shared.ClassStruct;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Squirrel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Networking.Connectivity;
using static CollapseLauncher.InnerLauncherConfig;
using static Hi3Helper.Locale;
using static Hi3Helper.Logger;
using static Hi3Helper.Shared.Region.LauncherConfig;

namespace CollapseLauncher
{
    #region LauncherUpdateRegion
    internal static class LauncherUpdateWatcher
    {
        public static string UpdateChannelName;
        public static AppUpdateVersionProp UpdateProperty;
        private static LauncherUpdateInvoker invoker = new LauncherUpdateInvoker();
        public static void GetStatus(LauncherUpdateProperty e) => invoker!.GetStatus(e);
        public static bool isUpdateCooldownActive;
        public static bool isMetered
        {
            get
            {
                NetworkCostType currentNetCostType = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost()?.NetworkCostType ?? NetworkCostType.Fixed;
                return !(currentNetCostType == NetworkCostType.Unrestricted || currentNetCostType == NetworkCostType.Unknown);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("ReSharper", "FunctionNeverReturns")]
        public static async void StartCheckUpdate(bool forceUpdate)
        {
            UpdateChannelName = IsPreview ? "preview" : "stable";
            while (true)
            {
                if ((!(GetAppConfigValue("DontAskUpdate").ToBoolNullable() ?? true) || ForceInvokeUpdate) && !IsSkippingUpdateCheck)
                {
                    try
                    {
                        // Force disable cooldown when its being forcefully updated
                        if (forceUpdate)
                            isUpdateCooldownActive = false;
                        // Stopping auto update when it was recently called. Workaround for update being called twice on metadata update.
                        if (!isUpdateCooldownActive)
                        {
                            if (!isMetered || forceUpdate)
                            {
                                isUpdateCooldownActive = true;
                                using (Updater updater = new Updater(UpdateChannelName))
                                {
                                    UpdateInfo info = await updater.StartCheck();
                                    GameVersion RemoteVersion = new GameVersion(info!.FutureReleaseEntry!.Version!.Version);

                                    AppUpdateVersionProp miscMetadata = await GetUpdateMetadata();
                                    UpdateProperty = new AppUpdateVersionProp { ver = RemoteVersion.VersionString, time = miscMetadata!.time };

                                    if (CompareVersion(AppCurrentVersion, RemoteVersion))
                                        GetStatus(new LauncherUpdateProperty { IsUpdateAvailable = true, NewVersionName = RemoteVersion });
                                    else
                                        GetStatus(new LauncherUpdateProperty { IsUpdateAvailable = false, NewVersionName = RemoteVersion });
                                }
                                ForceInvokeUpdate = false;
                            }
                            else
                            {
                                LogWriteLine($"Current network state is metered or disconnected! Auto update is skipped.\r\n\tPlease check your connection or use `Check for Update` button in Settings menu to update.", LogType.Warning, true);
                            }
                            isUpdateCooldownActive = true;
                        }
                        else LogWriteLine("Update was recently invoked! Stopping auto update until it resets in 15 minutes", LogType.Error, true);
                    }
                    catch (Exception ex)
                    {
                        LogWriteLine($"Update check has failed! Will retry in 15 mins.\r\n{ex}", LogType.Error, true);
                    }
                }
                // Delay for 15 to 60 minutes depending on metered
                await Task.Delay((isMetered ? 3600 : 900) * 1000);
                // Reset isUpdateRecentlyInvoked to release the lock
                isUpdateCooldownActive = false;
            }
        }

        private static async ValueTask<AppUpdateVersionProp> GetUpdateMetadata()
        {
            string relativePath = ConverterTool.CombineURLFromString(UpdateChannelName, "fileindex.json");
            await using BridgedNetworkStream ms = await FallbackCDNUtil.TryGetCDNFallbackStream(relativePath, default);
            return await ms!.DeserializeAsync<AppUpdateVersionProp>(InternalAppJSONContext.Default);
        }

        public static bool CompareVersion(GameVersion? CurrentVer, GameVersion? ComparedVer)
        {
            if (CurrentVer == null || ComparedVer == null) return false;
            return CurrentVer.Value.ToVersion() < ComparedVer.Value.ToVersion();
        }
    }

    public class AppUpdateVersionProp
    {
        public string ver { get; set; }
        public long time { get; set; }
        public List<AppUpdateVersionFileProp> f { get; set; }
    }

    public class AppUpdateVersionFileProp
    {
        public string p { get; set; }
        public string crc { get; set; }
        public long s { get; set; }
    }

    internal class LauncherUpdateInvoker
    {
        public static event EventHandler<LauncherUpdateProperty> UpdateEvent;
        public void GetStatus(LauncherUpdateProperty e) => UpdateEvent?.Invoke(this, e);
    }

    internal class LauncherUpdateProperty
    {
        public bool IsUpdateAvailable { get; set; }
        public GameVersion NewVersionName { get; set; }
        public bool QuitFromUpdateMenu { get; set; }
    }
    #endregion
    #region ThemeChangeRegion
    internal static class ThemeChanger
    {
        static ThemeChangerInvoker invoker = new ThemeChangerInvoker();
        public static void ChangeTheme(ElementTheme e)
        {
            CurrentAppTheme = e switch
            {
                ElementTheme.Light => AppThemeMode.Light,
                ElementTheme.Default => AppThemeMode.Default,
                _ => AppThemeMode.Dark
            };

            SetAppConfigValue("ThemeMode", CurrentAppTheme.ToString());
            invoker!.ChangeTheme(e);
        }
    }

    internal class ThemeChangerInvoker
    {
        public static event EventHandler<ThemeProperty> ThemeEvent;
        public void ChangeTheme(ElementTheme e) => ThemeEvent?.Invoke(this, new ThemeProperty(e));
    }

    internal class ThemeProperty
    {
        internal ThemeProperty(ElementTheme e) => Theme = e;
        // ReSharper disable once UnusedAutoPropertyAccessor.Global
        public ElementTheme Theme { get; private set; }
    }
    #endregion
    #region ErrorSenderRegion
    public enum ErrorType { Unhandled, GameError, Connection, Warning }

    internal static class ErrorSender
    {
        static ErrorSenderInvoker invoker = new ErrorSenderInvoker();
        public static string ExceptionContent;
        public static ErrorType ExceptionType;
        public static string ExceptionTitle;
        public static string ExceptionSubtitle;
        public static void SendException(Exception e, ErrorType eT = ErrorType.Unhandled) => invoker!.SendException(e, eT);
        public static void SendWarning(Exception e, ErrorType eT = ErrorType.Warning) =>
            invoker!.SendException(e, eT);
        public static void SendExceptionWithoutPage(Exception e, ErrorType eT = ErrorType.Unhandled)
        {
            ExceptionContent = e!.ToString();
            ExceptionType = eT;
            SetPageTitle(eT);
        }

        public static void SetPageTitle(ErrorType errorType)
        {
            var _locUnhandledException = Lang!._UnhandledExceptionPage!;
            switch (errorType)
            {
                case ErrorType.Unhandled:
                    ExceptionTitle    = _locUnhandledException.UnhandledTitle1;
                    ExceptionSubtitle = _locUnhandledException.UnhandledTitle1;
                    break;
                case ErrorType.Connection:
                    ExceptionTitle    = _locUnhandledException.UnhandledTitle2;
                    ExceptionSubtitle = _locUnhandledException.UnhandledSubtitle2;
                    break;
                case ErrorType.GameError:
                    ExceptionTitle    = _locUnhandledException.UnhandledTitle3;
                    ExceptionSubtitle = _locUnhandledException.UnhandledSubtitle3;
                    break;
                case ErrorType.Warning:
                    ExceptionTitle    = _locUnhandledException.UnhandledTitle4;
                    ExceptionSubtitle = _locUnhandledException.UnhandledSubtitle4;
                    break;
            }
        }
    }

    internal class ErrorSenderInvoker
    {
        public static event EventHandler<ErrorProperties> ExceptionEvent;
        public void SendException(Exception e, ErrorType eT) => ExceptionEvent?.Invoke(this, new ErrorProperties(e, eT));
    }

    internal class ErrorProperties
    {
        internal ErrorProperties(Exception e, ErrorType errorType)
        {
            Exception                    = e;
            ExceptionString              = e?.ToString() ?? String.Empty;
            ErrorSender.ExceptionContent = ExceptionString;
            ErrorSender.ExceptionType    = errorType;
            ErrorSender.SetPageTitle(errorType);
        }
        public Exception Exception { get; private set; }
        public string ExceptionString { get; private set; }
    }
    #endregion
    #region MainFrameRegion
    internal static class MainFrameChanger
    {
        private static Type currentWindow;
        private static Type currentPage;
        static MainFrameChangerInvoker invoker = new MainFrameChangerInvoker();
        public static void ChangeWindowFrame(Type e) => ChangeWindowFrame(e, new DrillInNavigationTransitionInfo());
        public static void ChangeWindowFrame(Type e, NavigationTransitionInfo eT)
        {
            currentWindow = e;
            invoker!.ChangeWindowFrame(e, eT);
        }
        public static void ChangeMainFrame(Type e) => ChangeMainFrame(e, new DrillInNavigationTransitionInfo());
        public static void ChangeMainFrame(Type e, NavigationTransitionInfo eT)
        {
            currentPage = e;
            invoker!.ChangeMainFrame(e, eT);
        }

        public static void ReloadCurrentWindowFrame() => ChangeWindowFrame(currentWindow);
        public static void ReloadCurrentMainFrame() => ChangeMainFrame(currentPage);
    }

    internal class MainFrameChangerInvoker
    {
        public static event EventHandler<MainFrameProperties> WindowFrameEvent;
        public static event EventHandler<MainFrameProperties> FrameEvent;
        public void ChangeWindowFrame(Type e, NavigationTransitionInfo eT) => WindowFrameEvent?.Invoke(this, new MainFrameProperties(e, eT));
        public void ChangeMainFrame(Type e, NavigationTransitionInfo eT) => FrameEvent?.Invoke(this, new MainFrameProperties(e, eT));
    }

    internal class MainFrameProperties
    {
        internal MainFrameProperties(Type FrameTo, NavigationTransitionInfo Transition)
        {
            this.FrameTo = FrameTo;
            this.Transition = Transition;
        }
        public Type FrameTo { get; private set; }
        public NavigationTransitionInfo Transition { get; private set; }
    }
    #endregion
    #region NotificationPushRegion
    internal static class NotificationSender
    {
        static NotificationInvoker invoker = new NotificationInvoker();
        public static void SendNotification(NotificationInvokerProp e) => invoker!.SendNotification(e);
        public static void SendCustomNotification(int tagID, InfoBar infoBarUI) => invoker!.SendNotification(new NotificationInvokerProp
        {
            IsCustomNotif = true,
            CustomNotifAction = NotificationCustomAction.Add,
            Notification = new NotificationProp
            {
                MsgId = tagID,
            },
            OtherContent = infoBarUI
        });
        public static void RemoveCustomNotification(int tagID) => invoker!.SendNotification(new NotificationInvokerProp
        {
            IsCustomNotif = true,
            CustomNotifAction = NotificationCustomAction.Remove,
            Notification = new NotificationProp
            {
                MsgId = tagID,
            }
        });
    }

    internal class NotificationInvoker
    {
        public static event EventHandler<NotificationInvokerProp> EventInvoker;
        public void SendNotification(NotificationInvokerProp e) => EventInvoker?.Invoke(this, e);
    }

    public enum NotificationCustomAction { Add, Remove }
    public class NotificationInvokerProp
    {
        public TypedEventHandler<InfoBar, object> CloseAction { get; set; }
        public FrameworkElement OtherContent { get; set; }
        public NotificationProp Notification { get; set; }
        public bool IsAppNotif { get; set; } = true;
        public bool IsCustomNotif { get; set; }
        public NotificationCustomAction CustomNotifAction { get; set; }

    }
    #endregion
    #region BackgroundRegion
    internal static class BackgroundImgChanger
    {
        static BackgroundImgChangerInvoker invoker = new BackgroundImgChangerInvoker();
        public static async Task WaitForBackgroundToLoad() => await invoker!.WaitForBackgroundToLoad();
        public static void ChangeBackground(string ImgPath, bool IsCustom = true, bool IsForceRecreateCache = false, bool IsRequestInit = false, FileStream? fileStream = null) => invoker!.ChangeBackground(ImgPath, IsCustom, IsForceRecreateCache, IsRequestInit, fileStream);
        public static void ToggleBackground(bool Hide) => invoker!.ToggleBackground(Hide);
    }

    internal class BackgroundImgChangerInvoker
    {
        public static event EventHandler<BackgroundImgProperty> ImgEvent;
        public static event EventHandler<bool> IsImageHide;
        BackgroundImgProperty property;
        public async Task WaitForBackgroundToLoad() => await Task.Run(() => { while (!property!.IsImageLoaded) { } });
        public void ChangeBackground(string ImgPath, bool IsCustom, bool IsForceRecreateCache = false, bool IsRequestInit = false, FileStream? fileStream = null) => ImgEvent?.Invoke(this, property = new BackgroundImgProperty(ImgPath, IsCustom, IsForceRecreateCache, IsRequestInit, fileStream));
        public void ToggleBackground(bool Hide) => IsImageHide?.Invoke(this, Hide);
    }

    internal class BackgroundImgProperty
    {
        internal BackgroundImgProperty(string ImgPath, bool IsCustom, bool IsForceRecreateCache, bool IsRequestInit, FileStream? existingFileStream)
        {
            this.ImgPath              = ImgPath;
            this.IsCustom             = IsCustom;
            this.IsForceRecreateCache = IsForceRecreateCache;
            this.IsRequestInit        = IsRequestInit;
            this.ExistingFileStream   = existingFileStream;
        }

        public bool IsRequestInit { get; set; }
        public bool IsForceRecreateCache { get; set; }
        public bool IsImageLoaded { get; set; }
        public string ImgPath { get; private set; }
        public bool IsCustom { get; private set; }
        public FileStream? ExistingFileStream { get; private set; }
    }
    #endregion
    #region SpawnWebView2Region
    internal static class SpawnWebView2
    {
        static SpawnWebView2Invoker invoker = new SpawnWebView2Invoker();
        public static void SpawnWebView2Window(string URL, UIElement parentUI)
        {
            if (GetAppConfigValue("UseExternalBrowser").ToBool())
            {
                if (string.IsNullOrEmpty(URL)) return;
                parentUI!.DispatcherQueue!.TryEnqueue(() =>
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = URL,
                        UseShellExecute = true,
                    });
                });
            }
            else invoker!.SpawnWebView2Window(URL);
        }
    }

    internal class SpawnWebView2Invoker
    {
        public static event EventHandler<SpawnWebView2Property> SpawnEvent;
        public void SpawnWebView2Window(string URL) => SpawnEvent?.Invoke(this, new SpawnWebView2Property(URL));
    }

    internal class SpawnWebView2Property
    {
        internal SpawnWebView2Property(string URL) => this.URL = URL;

        public string URL { get; set; }
    }
    #endregion
    #region ShowLoadingPage
    internal static class ShowLoadingPage
    {
        static ShowLoadingPageInvoker invoker = new ShowLoadingPageInvoker();
        public static void ShowLoading(string Title, string Subtitle, bool Hide = false) => invoker!.ShowLoading(Hide, Title, Subtitle);
    }

    internal class ShowLoadingPageInvoker
    {
        public static event EventHandler<ShowLoadingPageProperty> PageEvent;
        public void ShowLoading(bool Hide, string Title, string Subtitle) => PageEvent?.Invoke(this, new ShowLoadingPageProperty(Hide, Title, Subtitle));
    }

    internal class ShowLoadingPageProperty
    {
        internal ShowLoadingPageProperty(bool Hide, string Title, string Subtitle)
        {
            this.Hide = Hide;
            this.Title = Title;
            this.Subtitle = Subtitle;
        }
        public bool Hide { get; private set; }
        public string Title { get; private set; }
        public string Subtitle { get; private set; }
    }
    #endregion
    #region ChangeTitleDragArea
    public enum DragAreaTemplate
    {
        Full,
        Default
    }

    internal static class ChangeTitleDragArea
    {
        static ChangeTitleDragAreaInvoker invoker = new ChangeTitleDragAreaInvoker();
        public static void Change(DragAreaTemplate Template) => invoker!.Change(Template);
    }

    internal class ChangeTitleDragAreaInvoker
    {
        public static event EventHandler<ChangeTitleDragAreaProperty> TitleBarEvent;
        public void Change(DragAreaTemplate Template) => TitleBarEvent?.Invoke(this, new ChangeTitleDragAreaProperty(Template));
    }

    internal class ChangeTitleDragAreaProperty
    {
        internal ChangeTitleDragAreaProperty(DragAreaTemplate Template)
        {
            this.Template = Template;
        }

        public DragAreaTemplate Template { get; private set; }
    }
    #endregion
    #region UpdateBindings
    internal static class UpdateBindings
    {
        static UpdateBindingsInvoker invoker = new UpdateBindingsInvoker();
        public static void Update() => invoker!.Update();
    }

    internal class UpdateBindingsInvoker
    {
        private static EventArgs DummyArgs = new();
        public static event EventHandler UpdateEvents;
        public void Update() => UpdateEvents?.Invoke(this, DummyArgs!);
    }
    #endregion
}
