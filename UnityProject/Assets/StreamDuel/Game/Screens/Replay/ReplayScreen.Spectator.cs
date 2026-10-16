using System;
using StreamDuel.Core;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class ReplayScreen
    {
        SpectatorClient spectator;
        IDisposable spectatorConnection;
        OnlineConnectionAttempt spectatorAttempt;
        ContentDb spectatorContent;
        Battle lastSpectatorPublication;
        TextField spectatorAddress, spectatorPack;
        IntegerField spectatorPort;
        Label spectatorStatus;
        Button watch, stopWatching;
        bool spectating, spectatorFailed;

        public bool Spectating => spectating;
        public bool SpectatorFinished => !spectatorFailed && spectator?.IsFinished == true && spectator.Error == null
            && ReferenceEquals(lastSpectatorPublication, spectator.Battle) && displayedBattle?.Phase == Phase.Over;

        void BuildSpectatorEntry(VisualElement parent)
        {
            var row = Row();
            spectatorAddress = new TextField("观战地址") { name = "spectator-address", value = "127.0.0.1" };
            spectatorPort = new IntegerField("端口") { name = "spectator-port", value = 7778 };
            spectatorPack = new TextField("内容包")
            {
                name = "spectator-pack", value = args.Content?.PackId ?? settings.DefaultPack
            };
            spectatorAddress.style.width = 300;
            spectatorPort.style.width = 210;
            spectatorPack.style.width = 300;
            StyleField(spectatorAddress, spectatorAddress.labelElement, 100);
            StyleField(spectatorPort, spectatorPort.labelElement, 64);
            StyleField(spectatorPack, spectatorPack.labelElement, 100);
            row.Add(spectatorAddress);
            row.Add(spectatorPort);
            row.Add(spectatorPack);
            watch = AddButton(row, "连接观战", "watch-button", () => Attempt(ConnectSpectator));
            stopWatching = AddButton(row, "停止观战", "stop-watching-button", () => Attempt(() =>
            {
                CloseSpectator();
                animator?.SkipToEnd();
                displayedBattle = null;
                if (navigator != null) RefreshFrame();
                spectatorStatus.text = "已停止观战。";
                UpdateControls();
            }));
            parent.Add(row);
            spectatorStatus = Text("观战只显示主持端延迟公开的画面，不写入战绩或录像。", "spectator-status");
            parent.Add(spectatorStatus);
        }

        void ConnectSpectator()
        {
            if (Spectating) return;
            if (string.IsNullOrWhiteSpace(spectatorAddress.value))
                throw new ArgumentException("请填写观战地址。");
            if (spectatorPort.value < 1 || spectatorPort.value > 65535)
                throw new ArgumentException("观战端口必须是 1～65535。");
            string packId = spectatorPack.value?.Trim();
            string reason = UnityPackPolicy.Load().PackReason(packId);
            if (reason != null) throw new PackDisabledException(reason);
            var content = args.Content?.PackId == packId ? args.Content
                : (args.ResolvePack ?? fallbackResolver ?? ReplayStorage.ResolveBuiltIn)(packId);
            if (content == null) throw new InvalidOperationException("未找到观战所需的内容包。");
            StopPlayback();
            animator?.SkipToEnd();
            displayedBattle = null;
            spectating = true;
            spectatorFailed = false;
            spectatorContent = content;
            spectatorAttempt = new OnlineConnectionAttempt(false, spectatorAddress.value.Trim(), spectatorPort.value);
            feedback.style.display = DisplayStyle.None;
            spectatorStatus.text = "正在连接观战端口……";
            empty.text = "等待观战连接，不读取对战者尚未公开的状态。";
            UpdateControls();
        }

        void BeginSpectating(SpectatorClient client, IDisposable connection)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            StopPlayback();
            animator?.SkipToEnd();
            spectator = client;
            spectatorConnection = connection;
            spectating = true;
            spectatorFailed = false;
            displayedBattle = null;
            lastSpectatorPublication = null;
            feedback.style.display = DisplayStyle.None;
            spectatorStatus.text = "等待主持端公开延迟画面……";
            empty.text = "观战尚未收到已验证的公开画面。";
            UpdateControls();
        }

        void TickSpectator()
        {
            if (spectatorFailed) return;
            try
            {
                if (spectatorAttempt != null)
                {
                    if (!spectatorAttempt.TakeResult(out var transport, out var error, out _)) return;
                    spectatorAttempt.Dispose();
                    spectatorAttempt = null;
                    if (error != null) throw new InvalidOperationException(error);
                    try { BeginSpectating(new SpectatorClient(spectatorContent, transport), transport); }
                    catch { transport?.Dispose(); throw; }
                }
                if (spectator == null) return;
                // 验证、回放与延迟边界全部由 Core 处理；界面不接触 feed 或选手会话。
                spectator.Poll();
                if (spectator.Error != null) throw new InvalidOperationException(spectator.Error);
                var released = spectator.Battle;
                if (released != null && !ReferenceEquals(released, lastSpectatorPublication)
                    && animator?.IsPlaying != true)
                {
                    ReplayStorage.CheckPolicy(released.Record);
                    int logStart = displayedBattle?.Log.Count ?? 0;
                    int eventStart = displayedBattle?.Events.Count ?? 0;
                    bool animate = displayedBattle != null;
                    RenderBattle(released, logStart, eventStart, animate);
                    lastSpectatorPublication = released;
                    position.text = "观战 · 已公开至第 " + released.Turn + " 回合";
                }
                if (SpectatorFinished)
                {
                    spectatorStatus.text = "观战已结束：终局与结束消息均已验证。";
                    position.text = "观战 · 已结束";
                }
                else if (displayedBattle?.Phase == Phase.Over)
                {
                    spectatorStatus.text = "已收到公开终局，等待结束确认……";
                    stage.FieldBanner.text = "等待观战结束确认";
                }
                else spectatorStatus.text = released == null ? "等待主持端公开延迟画面……"
                    : "正在观战：只显示已验证的延迟画面。";
            }
            catch (Exception error)
            {
                spectatorFailed = true;
                spectatorAttempt?.Dispose();
                spectatorAttempt = null;
                spectatorConnection?.Dispose();
                spectatorConnection = null;
                animator?.SkipToEnd();
                spectatorStatus.text = "观战已中断，保留最后已验证画面；可停止观战或返回。";
                if (displayedBattle?.Phase == Phase.Over) stage.FieldBanner.text = "观战中断，终局未获确认";
                Message("观战失败：" + error.Message, true);
            }
            UpdateControls();
        }

        void UpdateSpectatorControls()
        {
            watch?.SetEnabled(!Spectating);
            stopWatching?.SetEnabled(Spectating);
            spectatorAddress?.SetEnabled(!Spectating);
            spectatorPort?.SetEnabled(!Spectating);
            spectatorPack?.SetEnabled(!Spectating);
            page?.Q<VisualElement>("replay-library-controls")?.SetEnabled(!Spectating);
            page?.Q<VisualElement>("replay-share-controls")?.SetEnabled(!Spectating);
        }

        void CloseSpectator()
        {
            spectatorAttempt?.Dispose();
            spectatorAttempt = null;
            spectatorConnection?.Dispose();
            spectatorConnection = null;
            spectator = null;
            spectatorContent = null;
            lastSpectatorPublication = null;
            spectating = false;
            spectatorFailed = false;
        }
    }
}
