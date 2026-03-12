namespace LocalProxyServer
{
    public static class DnsMessageParser
    {
        public static bool TryGetMinimumTtl(byte[] response, out int minTtlSeconds)
        {
            minTtlSeconds = 0;

            if (response.Length < 12)
            {
                return false;
            }

            ushort qdCount = ReadUInt16(response, 4);
            ushort anCount = ReadUInt16(response, 6);
            ushort nsCount = ReadUInt16(response, 8);
            ushort arCount = ReadUInt16(response, 10);

            int offset = 12;

            for (int i = 0; i < qdCount; i++)
            {
                if (!SkipName(response, ref offset))
                {
                    return false;
                }

                if (!SkipBytes(response, ref offset, 4))
                {
                    return false;
                }
            }

            int totalRecords = anCount + nsCount + arCount;
            if (totalRecords == 0)
            {
                return true;
            }

            int? minTtl = null;

            for (int i = 0; i < totalRecords; i++)
            {
                if (!SkipName(response, ref offset))
                {
                    return false;
                }

                if (!EnsureAvailable(response, offset, 10))
                {
                    return false;
                }

                offset += 4; // type + class
                int ttl = ReadInt32(response, offset);
                if (ttl < 0)
                {
                    ttl = 0;
                }

                minTtl = minTtl.HasValue ? Math.Min(minTtl.Value, ttl) : ttl;
                offset += 4;

                ushort rdLength = ReadUInt16(response, offset);
                offset += 2;

                if (!SkipBytes(response, ref offset, rdLength))
                {
                    return false;
                }
            }

            minTtlSeconds = minTtl ?? 0;
            return true;
        }

        private static bool SkipName(byte[] message, ref int offset)
        {
            while (true)
            {
                if (!EnsureAvailable(message, offset, 1))
                {
                    return false;
                }

                byte length = message[offset];
                if (length == 0)
                {
                    offset += 1;
                    return true;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (!EnsureAvailable(message, offset, 2))
                    {
                        return false;
                    }

                    offset += 2;
                    return true;
                }

                offset += 1;
                if (!SkipBytes(message, ref offset, length))
                {
                    return false;
                }
            }
        }

        private static bool SkipBytes(byte[] message, ref int offset, int length)
        {
            if (!EnsureAvailable(message, offset, length))
            {
                return false;
            }

            offset += length;
            return true;
        }

        private static bool EnsureAvailable(byte[] message, int offset, int length)
        {
            return offset >= 0 && length >= 0 && offset + length <= message.Length;
        }

        private static ushort ReadUInt16(byte[] message, int offset)
        {
            return (ushort)((message[offset] << 8) | message[offset + 1]);
        }

        private static int ReadInt32(byte[] message, int offset)
        {
            return (message[offset] << 24) |
                   (message[offset + 1] << 16) |
                   (message[offset + 2] << 8) |
                   message[offset + 3];
        }
    }
}
