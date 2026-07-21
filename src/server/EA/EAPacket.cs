using System.Text;

namespace Arcadia.EA;

public readonly struct Packet
{
    public const int HEADER_SIZE = 12;

    public Dictionary<string, string> DataDict { get; }
    public string this[string key]
    {
        get => DataDict.GetValueOrDefault(key) ?? string.Empty;
        set => DataDict[key] = value;
    }

    public string TXN => DataDict.GetValueOrDefault(nameof(TXN)) ?? string.Empty;

    public Packet(byte[] packet)
    {
        Type = Encoding.ASCII.GetString(packet, 0, 4);

        var headerSplit = Utils.SplitAt(packet, HEADER_SIZE);
        Checksum = headerSplit[0][4..];

        var bigEndianChecksum = (BitConverter.IsLittleEndian ? Checksum.Reverse().ToArray() : Checksum).AsSpan();
        Length = BitConverter.ToUInt32(bigEndianChecksum[..4]);
        var idAndTransmissionType = BitConverter.ToUInt32(bigEndianChecksum[4..]);
        TransmissionType = idAndTransmissionType & 0xff000000;
        Id = idAndTransmissionType & 0x00ffffff;

        Data = headerSplit[1][..((int)Length - HEADER_SIZE)];
        DataDict = Utils.ParseFeslPacketToDict(Data);
    }

    public Packet(string type, uint transmissionType, uint packetId, Dictionary<string, string>? dataDict = null, uint length = 0)
    {
        Type = type.Trim();
        TransmissionType = transmissionType;
        Id = packetId;
        DataDict = dataDict ?? [];
        Length = length;
    }

    public async Task<byte[]> Serialize()
    {
        var data = Utils.DataDictToPacketString(DataDict).ToString();
        var header = PacketUtils.BuildPacketHeader(Type, TransmissionType, Id, data);

        var dataBytes = Encoding.ASCII.GetBytes(data);

        using var response = new MemoryStream(header.Length + dataBytes.Length);

        await response.WriteAsync(header);
        await response.WriteAsync(dataBytes);
        await response.FlushAsync();

        return response.ToArray();
    }


    public string Type { get; }
    public uint Id { get; }
    public uint TransmissionType { get;  }
    public uint Length { get; }
    public byte[]? Data { get; }
    public byte[]? Checksum { get; }
}