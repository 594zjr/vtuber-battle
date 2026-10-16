using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class OnlineScreen : IScreen
    {
        readonly List<Action> unbind = new List<Action>();
        readonly List<Action> unbindTeam = new List<Action>();
        readonly List<ContentDb> packs = new List<ContentDb>();
        readonly List<DropdownField> slots = new List<DropdownField>();
        readonly Stopwatch phaseTime = new Stopwatch();
        readonly Stopwatch closeTime = new Stopwatch();
        ScreenRouter router;
        ScreenArgs navigation;
        VisualElement editor, battleRoot, teamRoot;
        Label status, teamNotice, packNotice;
        TextField nickname, address;
        IntegerField hostPort, joinPort;
        DropdownField pack;
        Button cancel;
        OnlineConnectionAttempt attempt;
        OnlineBattleView battleView;
        TcpTransport transport;
        List<StreamerDef> roster;
        List<string> pendingTeam;
        string pendingName;
        bool pendingHost, failed, disposed;

        public string Id => ScreenIds.Online;
        internal NetSession Session { get; private set; }
        internal ContentDb SelectedContent { get; private set; }
        internal bool IsConnecting => !failed && (attempt != null || Session?.State == NetState.Handshake);
        internal bool IsClosing { get; private set; }
        internal bool IsFinished { get; private set; }
        internal int ListeningPort { get; private set; }

        public VisualElement Build(ScreenRouter screenRouter, object args)
        {
            Dispose();
            disposed = false;
            failed = false;
            router = screenRouter;
            navigation = args as ScreenArgs ?? new ScreenArgs();
            var page = new VisualElement { name = "online-screen" };
            page.AddToClassList("screen");
            var panel = new VisualElement();
            panel.AddToClassList("screen-panel");
            panel.style.height = Length.Percent(100);
            page.Add(panel);
            var title = OnlineBattleView.Text("局域网对战");
            title.AddToClassList("screen-title");
            title.style.unityFont = router.Resources.BoldFont;
            panel.Add(title);
            status = OnlineBattleView.Text("填写昵称与阵容，然后开房或加入。", true);
            status.name = "online-status";
            panel.Add(status);
            var scroll = new ScrollView { name = "online-content" };
            scroll.style.flexGrow = 1;
            scroll.style.minHeight = 0;
            panel.Add(scroll);
            editor = new VisualElement { name = "online-entry" };
            scroll.Add(editor);
            battleRoot = new VisualElement { name = "online-battle" };
            scroll.Add(battleRoot);
            battleView = new OnlineBattleView(battleRoot, Submit, SubmitReplace, Terminate, SendChat, SetChatMuted);
            var actions = new VisualElement();
            actions.AddToClassList("screen-actions");
            panel.Add(actions);
            cancel = AddButton(actions, "cancel-button", "取消 / 断开", Cancel);
            cancel.SetEnabled(false);
            AddButton(actions, "back-button", "返回", () => router.Show(navigation.ReturnScreenId, navigation.ReturnArgs));
            try { BuildEntry(); }
            catch (Exception ex) { Fail("内容包暂时无法读取：" + ex.Message, false); }
            return page;
        }

        void BuildEntry()
        {
            var rules = UnityEngine.Resources.Load<TextAsset>("StreamDuelRules");
            if (rules == null) throw new InvalidOperationException();
            var policy = UnityPackPolicy.Load();
            var warnings = new List<string>();
            foreach (var asset in UnityEngine.Resources.LoadAll<TextAsset>("Packs")
                .Where(item => item.name != "LICENSE").OrderBy(item => item.name, StringComparer.Ordinal))
            {
                var loaded = PackLoader.LoadJson(rules.text, asset.text, asset.name, policy);
                if (loaded.Db != null) packs.Add(loaded.Db);
                else warnings.Add(loaded.Error);
            }
            if (!GameRuntimeSettings.IsSmokeTest)
                foreach (var loaded in PackLoader.LoadDirectory(rules.text, Path.Combine(Application.persistentDataPath, "packs"), policy))
                {
                    if (loaded.Error != null) { warnings.Add(loaded.Error); continue; }
                    if (packs.Any(existing => existing.PackId == loaded.Db.PackId))
                    {
                        warnings.Add("有外部内容包与内置包重复，已优先使用内置内容。");
                        continue;
                    }
                    packs.Add(loaded.Db);
                }
            if (packs.Count == 0) throw new InvalidOperationException("没有可用内容包，请检查内容文件与下架政策。");
            if (warnings.Count > 0) editor.Add(OnlineBattleView.Text(string.Join("\n", warnings), true));
            editor.Add(OnlineBattleView.Text("双方需选择相同内容包和规则。仅连接可信的本机或局域网设备；本版本不提供公网匹配。", true));
            nickname = new TextField("昵称") { name = "nickname", value = "玩家", maxLength = 24 };
            editor.Add(nickname);
            StyleInput(nickname);
            int selected = packs.FindIndex(item => item.PackId == GameRuntimeSettings.Current.DefaultPack);
            pack = new DropdownField("内容包", packs.Select(item => item.PackName + " [" + item.PackId + "]").ToList(), Math.Max(0, selected)) { name = "pack" };
            editor.Add(pack);
            StyleInput(pack);
            packNotice = OnlineBattleView.Text("", true);
            editor.Add(packNotice);
            teamRoot = new VisualElement { name = "online-team" };
            teamRoot.style.flexDirection = FlexDirection.Row;
            teamRoot.style.flexWrap = Wrap.Wrap;
            teamRoot.style.justifyContent = Justify.SpaceBetween;
            editor.Add(teamRoot);
            teamNotice = OnlineBattleView.Text("", true);
            teamNotice.name = "online-team-notice";
            editor.Add(teamNotice);
            var packChanged = new EventCallback<ChangeEvent<string>>(_ => SelectPack());
            pack.RegisterValueChangedCallback(packChanged);
            unbind.Add(() => pack.UnregisterValueChangedCallback(packChanged));
            SelectPack();
            editor.Add(OnlineBattleView.Text("第 1 号位首发。阵容不会写入本机存档。", true));
            var connection = new VisualElement();
            connection.style.flexDirection = FlexDirection.Row;
            connection.style.flexWrap = Wrap.Wrap;
            editor.Add(connection);
            var hostBox = new VisualElement();
            hostBox.style.flexGrow = 1;
            hostBox.style.minWidth = 380;
            connection.Add(hostBox);
            hostPort = new IntegerField("开房端口") { name = "host-port", value = 24680 };
            hostBox.Add(hostPort);
            StyleInput(hostPort);
            hostBox.Add(OnlineBattleView.Text("端口填 0 可自动分配。开房后会显示可分享的 IP 与实际端口。", true));
            AddButton(hostBox, "host-button", "开房等待", () => Begin(true));
            var joinBox = new VisualElement();
            joinBox.style.flexGrow = 1;
            joinBox.style.minWidth = 380;
            connection.Add(joinBox);
            address = new TextField("对方 IP") { name = "address", value = "127.0.0.1" };
            joinBox.Add(address);
            StyleInput(address);
            joinPort = new IntegerField("对方端口") { name = "join-port", value = 24680 };
            joinBox.Add(joinPort);
            StyleInput(joinPort);
            AddButton(joinBox, "join-button", "加入房间", () => Begin(false));
            BuildDiscovery();
            if (!string.IsNullOrEmpty(GameRuntimeSettings.Notice)) editor.Add(OnlineBattleView.Text(GameRuntimeSettings.Notice, true));
        }

        void SelectPack()
        {
            if (pack.index < 0 || pack.index >= packs.Count) return;
            SelectedContent = packs[pack.index];
            roster = SelectedContent.Streamers.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
            foreach (var remove in unbindTeam) remove();
            unbindTeam.Clear();
            teamRoot.Clear();
            slots.Clear();
            var labels = roster.Select((item, index) => (index + 1) + ". " + item.Name + " · " + Describe.Tracks(SelectedContent, item)).ToList();
            packNotice.text = SelectedContent.PackNotice ?? "";
            List<string> suggested;
            try { suggested = TeamBuilder.Suggest(SelectedContent, null, Array.Empty<string>(), 1); }
            catch (InvalidOperationException)
            {
                teamNotice.text = "此内容包无法组成合法的六人阵容，请选择其他内容包。";
                return;
            }
            for (int i = 0; i < 6; i++)
            {
                var slot = new DropdownField("第 " + (i + 1) + " 号位", labels, roster.FindIndex(item => item.Id == suggested[i]))
                    { name = "team-slot-" + i };
                StyleInput(slot);
                slot.style.width = Length.Percent(48);
                slot.style.marginLeft = slot.style.marginRight = 0;
                var changed = new EventCallback<ChangeEvent<string>>(_ => ValidateTeam());
                slot.RegisterValueChangedCallback(changed);
                unbindTeam.Add(() => slot.UnregisterValueChangedCallback(changed));
                slots.Add(slot);
                teamRoot.Add(slot);
            }
            ValidateTeam();
        }

        List<string> ReadTeam()
        {
            return slots.Select(slot => slot.index >= 0 && slot.index < roster.Count ? roster[slot.index].Id : null).ToList();
        }

        void ValidateTeam()
        {
            var errors = TeamCheck.Validate(SelectedContent, null, ReadTeam());
            teamNotice.text = errors.Count == 0 ? "六人阵容可用。" : string.Join("；", errors);
        }

        void Begin(bool host)
        {
            if (attempt != null || Session != null || disposed || failed) return;
            string name = nickname.value?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            {
                status.text = "请输入不含换行或控制字符的昵称。";
                return;
            }
            var team = ReadTeam();
            var errors = TeamCheck.Validate(SelectedContent, null, team);
            if (errors.Count > 0) { status.text = string.Join("；", errors); return; }
            int port = host ? hostPort.value : joinPort.value;
            if (port < (host ? 0 : 1) || port > 65535)
            {
                status.text = host ? "开房端口必须为 0～65535。" : "对方端口必须为 1～65535。";
                return;
            }
            string ip = address.value?.Trim() ?? "";
            if (ip.StartsWith("[", StringComparison.Ordinal) && ip.EndsWith("]", StringComparison.Ordinal))
                ip = ip.Substring(1, ip.Length - 2);
            if (!host && !IPAddress.TryParse(ip, out _))
            {
                status.text = "请输入有效的 IPv4 或 IPv6 地址。";
                return;
            }
            try { openingRating = LoadRating(); }
            catch (Exception ex) { status.text = "本机档案无法读取：" + TextInput.Clean(ex.Message, TextInputKind.Note).Text; return; }
            StopDiscovery();
            failed = false;
            IsFinished = IsClosing = false;
            settlementMessage = chatNotice = null;
            renderedSignature = null;
            pendingHost = host;
            pendingTeam = team;
            pendingName = name;
            pendingAddress = ip;
            pendingPort = port;
            pendingSettings = GameSettings.FromJson(GameRuntimeSettings.Current.ToJson());
            battleRoot.SetEnabled(true);
            editor.SetEnabled(false);
            cancel.SetEnabled(true);
            ListeningPort = 0;
            attempt = new OnlineConnectionAttempt(host, ip, port);
            status.text = host ? "正在创建房间……" : "正在连接对方……";
        }

        public void Tick(float dt)
        {
            if (disposed || failed || IsFinished) return;
            try { TickDiscovery(); TickNetwork(); }
            catch (Exception ex) { Fail("联网操作失败：" + ex.Message, false); }
        }

        void RefreshBattle()
        {
            if (Session?.Battle == null) return;
            string signature = ViewSignature();
            if (renderedSignature == signature) return;
            renderedSignature = signature;
            battleView.Refresh(Session, IsFinished || IsClosing, settlementMessage, chatNotice);
        }

        void Submit(BattleAction action)
        {
            if (!RefreshInput() || Session.State != NetState.Choose || Session.HasSubmitted) return;
            try { Session.Submit(action); Tick(0); }
            catch (Exception) { Fail("提交行动失败，连接已关闭。请返回重试。", false); }
        }

        void SubmitReplace(int target)
        {
            if (!RefreshInput() || Session.State != NetState.Replace || Session.HasSubmitted) return;
            try { Session.SubmitReplace(target); Tick(0); }
            catch (Exception) { Fail("提交补位失败，连接已关闭。请返回重试。", false); }
        }

        void Fail(string reason, bool restoreEntry)
        {
            failed = true;
            IsClosing = false;
            battleRoot.SetEnabled(false);
            status.text = TextInput.Clean(reason, TextInputKind.Note).Text;
            attempt?.Dispose();
            attempt = null;
            StopBeacon();
            CloseListener();
            CloseTransport();
            if (restoreEntry) { failed = false; editor.SetEnabled(true); }
            cancel.SetEnabled(Session != null);
        }

        void Cancel()
        {
            LeaveSession();
            attempt?.Dispose();
            attempt = null;
            CloseTransport();
            CloseListener();
            StopBeacon();
            StopDiscovery();
            Session = null;
            IsClosing = IsFinished = failed = false;
            ListeningPort = 0;
            renderedSignature = null;
            settlementMessage = chatNotice = null;
            phaseTime.Reset();
            closeTime.Reset();
            battleView.Dispose();
            battleRoot.SetEnabled(true);
            editor.style.display = DisplayStyle.Flex;
            editor.SetEnabled(true);
            cancel.SetEnabled(false);
            status.text = "连接已取消或断开，可以重新开房或加入。";
        }

        void CloseTransport()
        {
            var closing = transport;
            transport = null;
            // 释放会等待接收线程退出，把这个等待留在后台，返回按钮无需等网络结束。
            if (closing != null) Task.Run(() => closing.Dispose());
        }

        Button AddButton(VisualElement parent, string name, string text, Action action)
        {
            var button = new Button { name = name, text = text };
            button.AddToClassList("screen-button");
            button.Query<Label>().ForEach(label => label.enableRichText = false);
            button.clicked += action;
            unbind.Add(() => button.clicked -= action);
            parent.Add(button);
            return button;
        }

        static void StyleInput(VisualElement control)
        {
            control.style.flexDirection = FlexDirection.Column;
            control.style.minHeight = 52;
            control.style.height = StyleKeyword.Auto;
            control.style.flexShrink = 0;
            control.style.minWidth = 0;
            control.style.fontSize = 24;
            control.style.marginTop = 4;
            control.style.marginBottom = 10;
            control.style.whiteSpace = WhiteSpace.Normal;
            var input = control.Q(className: "unity-base-field__input");
            if (input != null)
            {
                input.style.minHeight = 48;
                input.style.paddingLeft = input.style.paddingRight = 12;
                input.style.backgroundColor = (Color)new Color32(28, 37, 73, 255);
                input.style.color = (Color)new Color32(232, 235, 250, 255);
            }
            control.Query<Label>().ForEach(label =>
            {
                label.enableRichText = false;
                label.style.whiteSpace = WhiteSpace.Normal;
            });
        }

        public void Dispose()
        {
            disposed = true;
            LeaveSession();
            attempt?.Dispose();
            attempt = null;
            CloseTransport();
            CloseListener();
            StopBeacon();
            StopDiscovery();
            ClearRoomBindings();
            battleView?.Dispose();
            battleView = null;
            Session = null;
            foreach (var remove in unbind) remove();
            unbind.Clear();
            foreach (var remove in unbindTeam) remove();
            unbindTeam.Clear();
            slots.Clear();
            packs.Clear();
            IsClosing = IsFinished = false;
            ListeningPort = 0;
            renderedSignature = null;
            discoveredRooms.Clear();
            roomSignature = null;
            discoveryTime.Reset();
            roomRoot = null;
            networkTime.Reset();
            phaseTime.Reset();
            closeTime.Reset();
        }
    }
}
