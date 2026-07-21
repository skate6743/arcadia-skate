using System.Buffers.Binary;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Microsoft.Extensions.Logging;

// MT_GameAttributes, MT_GameResetAttributes, MT_GameAttributeUpdate (S2 only).
namespace Arcadia.Hosting.Lobby.Walker.Handlers
{
    public static class AttributesHandlers
    {
        public static bool HandleGameAttributes(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            int attrSlotCount = Sk8MessageLayout.AttributeSlotCount(ctx.Server.Variant);
            int attrTrailerBytes = ctx.Server.Variant == GameVariant.Skate1
                ? Sk8MessageLayout.AttributeListSkate1LockTimeTrailerBytes : 0;

            int attrLen = Sk8MessageLayout.ProbeAttributeListLength(
                ctx.Work.AsSpan(ctx.PayloadBase, ctx.UserBodyLen),
                ctx.MsgOffset + 1,
                attrSlotCount,
                Sk8MessageLayout.AttributeListMaxBytesPerSlot);
            if (attrLen < 0) return false;
            if (ctx.MsgOffset + 1 + attrLen + attrTrailerBytes > ctx.UserBodyLen) return false;
            msgLen = 1 + attrLen + attrTrailerBytes;

            ctx.Logger.LogInformation(
                "LobbyUdp[{lobby}] APP-MSG GameAttributes {ep} uid={uid} bodyLen={n} trailer={tr}B",
                ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, attrLen, attrTrailerBytes);
            return true;
        }

        public static bool HandleGameResetAttributes(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            int attrSlotCount = Sk8MessageLayout.AttributeSlotCount(ctx.Server.Variant);

            int attrLen = Sk8MessageLayout.ProbeAttributeListLength(
                ctx.Work.AsSpan(ctx.PayloadBase, ctx.UserBodyLen),
                ctx.MsgOffset + 1,
                attrSlotCount,
                Sk8MessageLayout.AttributeListMaxBytesPerSlot);
            if (attrLen < 0) return false;
            msgLen = 1 + attrLen;

            ctx.Logger.LogInformation(
                "LobbyUdp[{lobby}] APP-MSG GameResetAttributes {ep} uid={uid} bodyLen={n}",
                ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, attrLen);
            return true;
        }

        public static bool HandleGameAttributeUpdateSkate2(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            if (ctx.MsgOffset + 1 + 4 + 4 > ctx.UserBodyLen) return false;

            Sk8AttributeType attr = (Sk8AttributeType)BinaryPrimitives.ReadInt32BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 1, 4));
            int strLen = (int)BinaryPrimitives.ReadUInt32BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 5, 4));
            if (strLen < 0 || strLen > Sk8MessageLayout.AttributeListMaxBytesPerSlot
                || ctx.MsgOffset + 1 + 4 + 4 + strLen > ctx.UserBodyLen)
                return false;

            ReadOnlySpan<byte> valBytes = ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 9, strLen);
            string valStr = System.Text.Encoding.UTF8.GetString(valBytes);
            ctx.Logger.LogInformation(
                "LobbyUdp[{lobby}] APP-MSG GameAttributeUpdate {ep} uid={uid} attr={attr} value={val}",
                ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, attr, valStr);
            ctx.Server.OnAttributeUpdate(attr, valStr);
            msgLen = 1 + 4 + 4 + strLen;
            return true;
        }
    }
}
