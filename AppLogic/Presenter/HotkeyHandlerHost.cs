﻿// Copyright (C) 2020 by Postprintum Pty Ltd (https://www.postprintum.com),
// which licenses this file to you under Apache License 2.0,
// see the LICENSE file in the project root for more information. 
// Author: Andrew Nosenko (@noseratio)

#nullable enable

using AppLogic.Config;
using AppLogic.Helpers;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AppLogic.Presenter
{
    [System.ComponentModel.DesignerCategory("")]
    internal partial class HotkeyHandlerHost :
        Container,
        IMessageFilter,
        IHotkeyHandlerHost
    {
        // classes which provide hotkey handlers
        private int _hotkeyId = 0; // IDs for Win32 RegisterHotKey

        // SemaphoreSlim as an async lock for hotkey handlers to avoid re-rentrancy
        private readonly SemaphoreSlim _asyncLock = new SemaphoreSlim(1);

        // when this is signalled, the container's RunAsync exits
        private readonly CancellationTokenSource _cts;

        // cancellation for RunAsync
        private CancellationToken Token => _cts.Token;

        private ContextMenuStrip Menu => _menu.Value;

        private Notepad Notepad => _notepad.Value;

        // the task of RunAsync
        private Task Completion { get; }

        // for playing sound notifictions
        private readonly Lazy<SoundPlayer?> _soundPlayer;

        // map hotkey Name to handler
        private readonly Dictionary<string, HotkeyHandler> _handlersByHotkeyNameMap =
            new Dictionary<string, HotkeyHandler>();

        // map hotkey ID to handler
        private readonly Dictionary<int, HotkeyHandler> _handlersByHotkeyIdMap =
            new Dictionary<int, HotkeyHandler>();

        private readonly Lazy<ContextMenuStrip> _menu;

        private readonly Lazy<Notepad> _notepad;

        private void Quit() => _cts.Cancel();

        private ValueTask<IDisposable> WithLockAsync() => 
            Disposable.CreateAsync(
                    () => _asyncLock.WaitAsync(this.Token),
                    () => _asyncLock.Release());

        // standard hotkey handler providers
        private static IEnumerable<Type> GetHotkeyHandlerProviders()
        {
            yield return typeof(PredefinedHotkeyHandlers);
            yield return typeof(ScriptHotkeyHandlers);
        }

        public HotkeyHandlerHost(CancellationToken token)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _soundPlayer = new Lazy<SoundPlayer?>(CreateSoundPlayer, isThreadSafe: false);
            _menu = new Lazy<ContextMenuStrip>(CreateContextMenu, isThreadSafe: false);
            _notepad = new Lazy<Notepad>(CreateNotepad, isThreadSafe: false);

            this.Completion = RunAsync();
        }

        public Task AsTask()
        {
            return this.Completion;
        }

        private void RegisterWindowsHotkey(HotkeyHandler hotkeyHandler)
        {
            var hotkey = hotkeyHandler.Hotkey;
            if (!hotkey.HasHotkey)
            {
                throw new InvalidOperationException(nameof(RegisterWindowsHotkey));
            }

            if (WinApi.RegisterHotKey(IntPtr.Zero, ++_hotkeyId,
                hotkey.Mods!.Value | WinApi.MOD_NOREPEAT,
                hotkey.Vkey!.Value))
            {
                _handlersByHotkeyIdMap.Add(_hotkeyId, hotkeyHandler);
            }
            else
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                throw new WarningException($"{hotkeyHandler.Hotkey.Name}: {error.Message}", error);
            }
        }

        private void SetCurrentFolder()
        {
            if (Configuration.TryGetOption("currentFolder", out var folder))
            {
                folder = Environment.ExpandEnvironmentVariables(folder);
            }
            if (folder == null || !Directory.Exists(folder))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);           
            }
            Directory.SetCurrentDirectory(folder);
        }

        private void InitializeHotkeys()
        {
            // Roaming config gets precedence over Local
            var hotkeys = Configuration.RoamingHotkeys.Union(Configuration.LocalHotkeys).ToArray();

            // instantiate the known hotkey handler providers
            var providers = GetHotkeyHandlerProviders()
                .Select(type => Activator.CreateInstance(type, this))
                .OfType<IHotkeyHandlerProvider>().ToArray();

            foreach (var hotkey in hotkeys)
            {
                foreach (var provider in providers)
                {
                    if (provider.CanHandle(hotkey, out var callback))
                    {
                        var handler = new HotkeyHandler(hotkey, callback);
                        _handlersByHotkeyNameMap.Add(hotkey.Name, handler);
                        if (hotkey.HasHotkey)
                        {
                            RegisterWindowsHotkey(handler);
                        }
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            _cts.Dispose();

            foreach (var hotkeyId in _handlersByHotkeyIdMap.Keys)
            {
                WinApi.UnregisterHotKey(IntPtr.Zero, hotkeyId);
            }

            _handlersByHotkeyNameMap.Clear();
            _handlersByHotkeyIdMap.Clear();

            if (_notepad.IsValueCreated)
            {
                _notepad.Value.Dispose();
            }

            base.Dispose(disposing);
        }

        private SoundPlayer? CreateSoundPlayer()
        {
            if (!Configuration.GetOption("playNotificationSound", defaultValue: true))
            { 
                return null;
            }

            if (!Configuration.TryGetOption("notifySound", out var soundPath))
            {
                return null;
            }

            SoundPlayer? soundPlayer = null;
            var filePath = Environment.ExpandEnvironmentVariables(soundPath);
            if (File.Exists(filePath))
            {
                soundPlayer = new SoundPlayer();
                try
                {
                    soundPlayer.SoundLocation = filePath;
                    this.Add(soundPlayer);
                }
                catch
                {
                    soundPlayer.Dispose();
                    throw;
                }
            }

            return soundPlayer;
        }

        private const string AUTORUN_REGKEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        private static bool IsAutorun()
        {
            var name = Application.ProductName;
            using var regKey = Registry.CurrentUser.OpenSubKey(AUTORUN_REGKEY, writable: false);
            var value = regKey.GetValue(name, String.Empty)?.ToString();
            return value.IsNotNullNorEmpty() &&
                File.Exists(value) &&
                String.Compare(
                    Path.GetFullPath(value.Trim()),
                    Path.GetFullPath(Diagnostics.GetExecutablePath()),
                    StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static void SetAutorun()
        {
            var name = Application.ProductName;
            var value = Diagnostics.GetExecutablePath();
            using var regKey = Registry.CurrentUser.OpenSubKey(AUTORUN_REGKEY, writable: true);
            regKey.SetValue(name, value, RegistryValueKind.String);
        }

        private static void ClearAutorun()
        {
            var name = Application.ProductName;
            using var regKey = Registry.CurrentUser.OpenSubKey(AUTORUN_REGKEY, writable: true);
            regKey.DeleteValue(name);
        }

        private async Task HandleHotkeyAsync(HotkeyHandler hotkeyHandler)
        {
            // try to get an instant async lock
            if (!await _asyncLock.WaitAsync(0, this.Token))
            {
                // discard this hotkey event, as we only allow 
                // one handler at a time to prevent re-entrancy
                return;
            }
            try
            {
                this.Menu.Close(ToolStripDropDownCloseReason.Keyboard);
                await InputHelpers.TimerYield(token: this.Token);
                await hotkeyHandler.Callback(hotkeyHandler.Hotkey, this.Token);
            }
            finally
            {
                _asyncLock.Release();
            }
        }

        bool IMessageFilter.PreFilterMessage(ref Message m)
        {
            switch (m.Msg)
            {
                case WinApi.WM_HOTKEY:
                    if (_handlersByHotkeyIdMap.TryGetValue((int)m.WParam, out var handler))
                    {
                        HandleHotkeyAsync(handler).IgnoreCancellations();
                        return true;
                    }
                    break;

                case WinApi.WM_QUIT:
                    Quit();
                    return true;

                default:
                    break;
            }
            return false;
        }

        #region Menu Handlers
        const string FEEDBACK_URL = "https://www.postprintum.com/devcomrade/feedback/";
        const string ABOUT_URL = "https://www.postprintum.com/devcomrade/";

        private delegate void MenuItemEventHandler(object s, EventArgs e);

        private delegate bool? MenuItemStateCallback();

        private static void About(object? s, EventArgs e) => Diagnostics.ShellExecute(ABOUT_URL);

        private static void None(object? s, EventArgs e) { }

        private static bool? None() => null;

        private static void Feedback(object? s, EventArgs e) => Diagnostics.ShellExecute(FEEDBACK_URL);

        private static void EditLocalConfig(object? s, EventArgs e) =>
            Diagnostics.ShellExecute(Configuration.LocalConfigPath);

        private static void EditRoamingConfig(object? s, EventArgs e)
        {
            var path = Configuration.RoamingConfigPath;
            if (!File.Exists(path) || File.ReadAllText(path).IsNullOrWhiteSpace())
            {
                // copy local config to roaming config
                var folder = Path.GetDirectoryName(path);
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                File.WriteAllText(path, Configuration.GetDefaultRoamingConfig(), Encoding.UTF8);
            }
            Diagnostics.ShellExecute(path);
        }
        private void OpenNotepad()
        {
            if (this.Notepad.Visible)
            {
                using var threadInputScope = AttachedThreadInputScope.Create();
                WinApi.SetForegroundWindow(Notepad.Handle);
            }
            else
            {
                this.Notepad.Show();
            }
        }

        private void Restart(object? s, EventArgs e)
        {
            Diagnostics.StartProcess(Diagnostics.GetExecutablePath());
            Quit();
        }

        private void RestartAsAdmin(object? s, EventArgs e)
        {
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = Diagnostics.GetExecutablePath(),
                Verb = "runas"
            };
            try
            {
                using var process = Process.Start(startInfo);
                Quit();
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != WinApi.ERROR_CANCELLED)
                {
                    throw;
                }
            }
        }

        private static void AutoStart(object? s, EventArgs e)
        {
            if (s is ToolStripMenuItem menuItem)
            {
                if (menuItem.Checked)
                {
                    ClearAutorun();
                    menuItem.Checked = false;
                }
                else
                {
                    SetAutorun();
                    menuItem.Checked = IsAutorun();
                }
            }
        }

        private void Exit(object? s, EventArgs e)
        {
            Quit();
        }

        private static (string, MenuItemEventHandler, MenuItemStateCallback) GetSeparatorMenuItem()
        {
            return ("-", None, None);
        }

        #endregion

        /// <summary>
        /// Provide tray menu items
        /// </summary>
        private IEnumerable<(string, MenuItemEventHandler, MenuItemStateCallback)> GetMenuItems()
        {
            // first add hotkey handlers which also have menuItem in the config file
            var handlers = _handlersByHotkeyNameMap.Values
                .Where(handler => handler.Hotkey.MenuItem.IsNotNullNorWhiteSpace())
                .ToArray();

            if (handlers.Length > 0)
            {
                foreach (var handler in handlers)
                {
                    var hotkey = handler.Hotkey;
                    string menuItemText;
                    if (hotkey.HasHotkey)
                    {
                        var hotkeyTitle = Utilities.GetHotkeyTitle(hotkey.Mods!.Value, hotkey.Vkey!.Value);
                        menuItemText = $"{hotkey.MenuItem}|{hotkeyTitle}";
                    }
                    else 
                    {
                        menuItemText = hotkey.MenuItem!;
                    }

                    yield return (
                        menuItemText, 
                        (s, e) => HandleHotkeyAsync(handler).IgnoreCancellations(), 
                        None);

                    if (hotkey.AddSeparator)
                    {
                        yield return GetSeparatorMenuItem();
                    }
                }
                yield return GetSeparatorMenuItem();
            }

            yield return ("Auto Start", AutoStart, () => IsAutorun());
            yield return ("Edit Local Config", EditLocalConfig, None);
            yield return ("Edit Roaming Config", EditRoamingConfig, None);
            yield return ("Restart", Restart, None);
            if (!Diagnostics.IsAdmin())
            {
                yield return ("Restart as Admin", RestartAsAdmin, None);
            }
            yield return ("Prevent Sleep Mode", Feedback, None);
            yield return GetSeparatorMenuItem();
            yield return ($"About {Application.ProductName}", About, None);
            yield return ("E&xit", Exit, None);
        }

        private EventHandler AsAsync(MenuItemEventHandler handler)
        {
            // we make all click handlers async because 
            // we want the menu to be dismissed first
            void handle(object s, EventArgs e)
            {
                async Task handleAsync()
                {
                    await InputHelpers.InputYield(token: this.Token);
                    handler(s, e);
                }
                handleAsync().IgnoreCancellations();
            }
            return handle!;
        }

        private ContextMenuStrip CreateContextMenu()
        {
            var contextMenu = new ContextMenuStrip();

            foreach (var (text, handler, queryState) in GetMenuItems())
            {
                if (text == "-")
                {
                    contextMenu.Items.Add(new ToolStripSeparator());
                }
                else
                {
                    var left = text;
                    var right = String.Empty;
                    var separator = text.LastIndexOf('|');
                    if (separator >= 0)
                    {
                        left = text.Substring(0, separator);
                        right = text.Substring(separator + 1);
                    }
                    var menuItem = new ToolStripMenuItem(left, image: null, AsAsync(handler));
                    menuItem.ShortcutKeyDisplayString = right;
                    var state = queryState();
                    if (state.HasValue)
                    {
                        menuItem.Checked = state.Value;
                    }
                    contextMenu.Items.Add(menuItem);
                }
            }

            return contextMenu;
        }

        private Notepad CreateNotepad()
        {
            var notepad = new Notepad();
            notepad.Show();
            return notepad;
        }

        private NotifyIcon CreateTrayIconMenu()
        {
            var notifyIcon = new NotifyIcon(this)
            {
                Text = Application.ProductName,
                ContextMenuStrip = this.Menu,
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Diagnostics.GetExecutablePath()),
            };

            return notifyIcon;
        }

        // async entry point
        private async Task RunAsync()
        {
            SetCurrentFolder();
            InitializeHotkeys();

            Application.AddMessageFilter(this);
            try
            {
                var trayIconMenu = CreateTrayIconMenu();
                this.Add(trayIconMenu);
                trayIconMenu.Visible = true;
                try
                {
                    // this infinte delay defines the async scope 
                    // for AddMessageFilter/RemoveMessageFilter
                    // the token is cancelled when the app exits
                    await Task.Delay(Timeout.Infinite, this.Token);
                }
                finally
                {
                    trayIconMenu.Visible = false;
                }
            }
            finally
            {
                Application.RemoveMessageFilter(this);
            }
        }

        bool IHotkeyHandlerHost.ClipboardContainsText()
        {
            return Clipboard.ContainsText();
        }

        string IHotkeyHandlerHost.GetClipboardText()
        {
            return Clipboard.GetText(TextDataFormat.UnicodeText);
        }

        void IHotkeyHandlerHost.ClearClipboard()
        {
            Clipboard.Clear();
        }

        void IHotkeyHandlerHost.SetClipboardText(string text)
        {
            Clipboard.SetText(text, TextDataFormat.UnicodeText);
        }

        async Task IHotkeyHandlerHost.FeedTextAsync(string text, CancellationToken token)
        {
            using var threadInputScope = AttachedThreadInputScope.Create();
            if (threadInputScope.IsAttached)
            {
                using (WaitCursorScope.Create())
                {
                    await KeyboardInput.WaitForAllKeysReleasedAsync(token);
                }
                await KeyboardInput.FeedTextAsync(text, token);
            }
        }

        void IHotkeyHandlerHost.PlayNotificationSound()
        {
            if (_soundPlayer.Value is SoundPlayer soundPlayer)
            {
                soundPlayer.Stop();
                soundPlayer.Play();
            }
        }

        void IHotkeyHandlerHost.ShowMenu()
        {
            async Task showMenuAsync()
            {
                // show the menu and await its dismissal
                using var @lock = await WithLockAsync();

                var tcs = new TaskCompletionSource<DBNull>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var rego = this.Token.Register(() => tcs.TrySetCanceled());

                using var scope = EventHandlerScope<ToolStripDropDownClosedEventHandler>.Create(
                    (s, e) => tcs.TrySetResult(DBNull.Value),
                    handler => this.Menu.Closed += handler,
                    handler => this.Menu.Closed -= handler);

                using (AttachedThreadInputScope.Create())
                {
                    // set thread focus to the menu window
                    await InputHelpers.TimerYield(token: this.Token);
                    WinApi.SetForegroundWindow(this.Menu.Handle);
                    this.Menu.Show(Cursor.Position);
                }

                await tcs.Task;
                await InputHelpers.TimerYield(token: this.Token);
            }

            showMenuAsync().IgnoreCancellations();
        }

        void IHotkeyHandlerHost.ShowNotepad(string? text)
        {
            OpenNotepad();
            if (text != null )
            {
                this.Notepad.TextBox.SelectAll();
                this.Notepad.TextBox.Paste(text);
            }
        }

        int IHotkeyHandlerHost.TabSize => 
            Configuration.GetOption("tabSize", 2);
    }
}
