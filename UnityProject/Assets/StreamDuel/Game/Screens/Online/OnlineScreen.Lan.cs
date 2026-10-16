using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StreamDuel.Core;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class OnlineScreen
    {
        readonly Stopwatch discoveryTime = new Stopwatch();
        readonly Stopwatch beaconTime = new Stopwatch();
        readonly List<Action> unbindRooms = new List<Action>();
        readonly Dictionary<string, LanRoom> discoveredRooms = new Dictionary<string, LanRoom>(StringComparer.Ordinal);
        LanBeaconListener discovery;
        LanBeaconSender beacon;
        VisualElement roomRoot;
        Label discoveryNotice;
        string roomSignature, beaconNotice;
        double previousBeacon;

        internal bool IsBroadcasting => beacon != null && beacon.Error == null;

        void BuildDiscovery()
        {
            editor.Add(OnlineBattleView.Text("房主使用设置中的回合计时秒数，额外总时间为 0；客机采用房主配置。", true));
            AddButton(editor, "discover-rooms", "搜索局域网房间（5 秒）", BeginDiscovery);
            discoveryNotice = OnlineBattleView.Text("支持同一局域网 UDP 房间广播；也可直接填写 IP 加入。", true);
            discoveryNotice.name = "lan-discovery-status";
            editor.Add(discoveryNotice);
            roomRoot = new VisualElement { name = "lan-rooms" };
            editor.Add(roomRoot);
        }

        void BeginDiscovery()
        {
            if (disposed || attempt != null || Session != null) return;
            StopDiscovery();
            ClearRoomBindings();
            roomRoot.Clear();
            discoveredRooms.Clear();
            roomSignature = null;
            discovery = new LanBeaconListener();
            if (discovery.Error != null)
            {
                discoveryNotice.text = discovery.Error;
                StopDiscovery();
                return;
            }
            discoveryTime.Restart();
            discoveryNotice.text = "正在搜索局域网房间……";
        }

        void TickDiscovery()
        {
            if (roomRoot == null || attempt != null || Session != null) return;
            double now = discoveryTime.Elapsed.TotalSeconds;
            if (discovery != null)
            {
                discovery.Poll(now);
                if (discovery.Error != null)
                {
                    discoveryNotice.text = discovery.Error;
                    StopDiscovery();
                }
                else
                {
                    foreach (var room in discovery.RoomList.Rooms(now)) discoveredRooms[room.Address + ":" + room.Port] = room;
                    if (now >= 5)
                    {
                        discoveryNotice.text = "搜索完成，发现 " + discoveredRooms.Count + " 个房间；失联后请重新搜索。";
                        StopDiscovery();
                    }
                }
            }
            RenderRooms(now);
        }

        void RenderRooms(double now)
        {
            var rooms = discoveredRooms.Values.OrderBy(room => room.DisplayName, StringComparer.Ordinal)
                .ThenBy(room => room.Address, StringComparer.Ordinal).ThenBy(room => room.Port).ToArray();
            string hash = SelectedContent?.ContentHash();
            string signature = hash + "|" + string.Join("|", rooms.Select(room => room.Address + ":" + room.Port
                + ":" + room.DisplayName + ":" + room.Protocol + ":" + room.ContentHash + ":" + room.Ruleset
                + ":" + (now - room.LastSeen <= 3)));
            if (roomSignature == signature) return;
            roomSignature = signature;
            ClearRoomBindings();
            roomRoot.Clear();
            foreach (var room in rooms)
            {
                bool online = now - room.LastSeen <= 3;
                bool compatible = room.Protocol == NetSession.ReconnectProtocolVersion && room.ContentHash == hash && room.Ruleset == "casual";
                string reason = !online ? "已失联，请重新搜索" : room.Protocol != NetSession.ReconnectProtocolVersion
                    ? "协议版本不兼容" : room.ContentHash != hash ? "内容包不一致，请选择匹配内容包"
                    : room.Ruleset != "casual" ? "当前入口不支持此赛制" : "加入房间";
                var button = new Button { name = "lan-room-" + room.Address + "-" + room.Port,
                    text = room.DisplayName + " · " + room.Address + ":" + room.Port + "\n" + reason };
                button.AddToClassList("screen-button");
                button.style.whiteSpace = WhiteSpace.Normal;
                button.style.minHeight = 68;
                button.style.flexShrink = 0;
                button.Query<Label>().ForEach(label => label.enableRichText = false);
                button.SetEnabled(online && compatible);
                Action join = () =>
                {
                    // 点击时重新确认公告有效期和所选包，过期按钮不能绕过入口校验。
                    if (discoveryTime.Elapsed.TotalSeconds - room.LastSeen > 3
                        || room.Protocol != NetSession.ReconnectProtocolVersion || room.ContentHash != SelectedContent.ContentHash()
                        || room.Ruleset != "casual") return;
                    address.value = room.Address;
                    joinPort.value = room.Port;
                    Begin(false);
                };
                button.clicked += join;
                unbindRooms.Add(() => button.clicked -= join);
                roomRoot.Add(button);
            }
        }

        void TickBeacon()
        {
            if (!pendingHost || ListeningPort == 0) return;
            if (beacon == null && beaconNotice == null)
            {
                beacon = new LanBeaconSender(new LanRoom
                {
                    Name = TextInput.Clean(pendingName + "的房间", TextInputKind.RoomName).Text,
                    Port = ListeningPort, Protocol = NetSession.ReconnectProtocolVersion,
                    ContentHash = SelectedContent.ContentHash(), Ruleset = "casual"
                });
                beaconTime.Restart();
                previousBeacon = 0;
            }
            if (beacon == null) return;
            double now = beaconTime.Elapsed.TotalSeconds;
            beacon.Tick(Math.Max(0, now - previousBeacon));
            previousBeacon = now;
            if (beacon.Error != null)
            {
                beaconNotice = beacon.Error + "仍可使用 IP 与端口直接加入。";
                beacon.Dispose();
                beacon = null;
            }
        }

        void StopBeacon()
        {
            beacon?.Dispose();
            beacon = null;
            beaconNotice = null;
            beaconTime.Reset();
        }

        void StopDiscovery()
        {
            discovery?.Dispose();
            discovery = null;
        }

        void ClearRoomBindings()
        {
            foreach (var remove in unbindRooms) remove();
            unbindRooms.Clear();
        }
    }
}
