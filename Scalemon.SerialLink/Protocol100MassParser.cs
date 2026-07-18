using System.Buffers.Binary;
using System;

namespace Scalemon.SerialLink;

internal sealed record Protocol100MassReading(
    decimal WeightKg,
    decimal DivisionKg,
    bool IsStable,
    bool IsNet,
    bool IsTerminalZero,
    decimal? TareKg);

internal static class Protocol100MassParser
{
    internal const byte CmdAckMassa = 0x24;

    internal static bool TryParse(
        ReadOnlySpan<byte> body,
        out Protocol100MassReading? reading,
        out string error)
    {
        reading = null;
        if (body.Length is not 9 and not 13)
        {
            error = $"Некорректная длина CMD_ACK_MASSA: {body.Length}";
            return false;
        }

        if (body[0] != CmdAckMassa)
        {
            error = $"Ожидался CMD_ACK_MASSA, получено 0x{body[0]:X2}";
            return false;
        }

        var divisionKg = body[5] switch
        {
            0 => 0.0001m,
            1 => 0.001m,
            2 => 0.01m,
            3 => 0.1m,
            4 => 1m,
            _ => 0m
        };

        if (divisionKg == 0m)
        {
            error = $"Некорректная цена деления: {body[5]}";
            return false;
        }

        var weightRaw = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(1, 4));
        decimal? tareKg = body.Length == 13
            ? BinaryPrimitives.ReadInt32LittleEndian(body.Slice(9, 4)) * divisionKg
            : null;

        reading = new Protocol100MassReading(
            weightRaw * divisionKg,
            divisionKg,
            body[6] != 0,
            body[7] != 0,
            body[8] != 0,
            tareKg);
        error = string.Empty;
        return true;
    }
}
