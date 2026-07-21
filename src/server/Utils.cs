using System.Text;

namespace Arcadia;

public static class Utils
{
    public static byte[][] SplitAt(byte[] source, int index)
    {
        byte[] first = new byte[index];
        byte[] second = new byte[source.Length - index];

        Array.Copy(source, 0, first, 0, index);
        Array.Copy(source, index, second, 0, source.Length - index);

        return [first, second];
    }

    public static Dictionary<string, string> ParseFeslPacketToDict(byte[] data)
    {
        var dataString = Encoding.ASCII.GetString(data);

        var dataSplit = dataString.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x.Replace("\0", string.Empty))).ToArray();
        dataSplit = dataSplit.Select(x => x.Replace("\0", string.Empty)).ToArray();

        var dataDict = new Dictionary<string, string>();
        for (var i = 0; i < dataSplit.Length; i++)
        {
            var entrySplit = dataSplit[i].Split('=', StringSplitOptions.TrimEntries);

            var parameter = entrySplit[0];
            var value = entrySplit.Length > 1 ? entrySplit[1].Replace(@"\", string.Empty) : string.Empty;

            dataDict.TryAdd(parameter, value);
        }

        return dataDict;
    }

    public static StringBuilder DataDictToPacketString(Dictionary<string, string> packetData)
    {
        var dataBuilder = new StringBuilder();

        for (var i = 0; i < packetData.Count; i++)
        {
            var line = packetData.ElementAt(i);
            var parameter = line.Key;
            var value = line.Value;

            dataBuilder.Append(parameter).Append('=');

            if (value.ToString()?.Contains(' ') == true)
            {
                dataBuilder.Append('"').Append(value).Append('"');
            }
            else
            {
                dataBuilder.Append(value);
            }

            dataBuilder.Append('\n');
        }

        if (dataBuilder.Length > 0)
        {
            dataBuilder.Remove(dataBuilder.Length - 1, 1);
        }

        dataBuilder.Append('\0');
        return dataBuilder;
    }

    public static int FindBytePattern(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> searchPattern, int offset = 0)
    {
        if (searchPattern.IsEmpty || buffer.Length < searchPattern.Length || offset > buffer.Length - searchPattern.Length)
        {
            return -1;
        }

        int endIndex = buffer.Length - searchPattern.Length + 1;
        for (int i = offset; i < endIndex; i++)
        {
            if (buffer[i] == searchPattern[0] && buffer.Slice(i, searchPattern.Length).SequenceEqual(searchPattern))
            {
                return i;
            }
        }

        return -1;
    }

}