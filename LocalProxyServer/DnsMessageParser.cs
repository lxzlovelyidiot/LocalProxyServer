namespace LocalProxyServer
{
    public static class DnsMessageParser
    {
        public static string GetCacheKey(byte[] message)
        {
            if (message.Length < 12)
            {
                return Convert.ToBase64String(message);
            }

            int offset = 12;
            if (TryParseName(message, ref offset, out var name))
            {
                // DNS names are case-insensitive, normalize to lowercase
                name = name.ToLowerInvariant();

                // Expect 4 more bytes for QTYPE and QCLASS
                if (offset + 4 <= message.Length)
                {
                    ushort qtype = (ushort)((message[offset] << 8) | message[offset + 1]);
                    ushort qclass = (ushort)((message[offset + 2] << 8) | message[offset + 3]);
                    return $"{name}:{qtype}:{qclass}";
                }

                return name;
            }

            // Fallback for malformed or unusual packets: 
            // clone and zero out Transaction ID to allow caching identical binary queries
            var keyBytes = (byte[])message.Clone();
            keyBytes[0] = 0;
            keyBytes[1] = 0;
            return Convert.ToBase64String(keyBytes);
        }

        public static string GetQueryName(byte[] message)
        {
            if (message.Length < 12)
            {
                return "unknown";
            }

            int offset = 12;
            if (TryParseName(message, ref offset, out var name))
            {
                return name;
            }

            return "unknown";
        }

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

        public static bool TryParseName(byte[] message, ref int offset, out string name)
        {
            var labels = new List<string>();
            int currentOffset = offset;
            int? firstPointerOffset = null;
            int jumps = 0;
            const int maxJumps = 16; // RFC doesn't specify, but 16 is plenty for legitimate names

            while (true)
            {
                if (!EnsureAvailable(message, currentOffset, 1))
                {
                    name = string.Empty;
                    return false;
                }

                byte length = message[currentOffset];
                if (length == 0)
                {
                    currentOffset += 1;
                    break;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (jumps++ > maxJumps || !EnsureAvailable(message, currentOffset, 2))
                    {
                        name = string.Empty;
                        return false;
                    }

                    int pointer = ((length & 0x3F) << 8) | message[currentOffset + 1];
                    if (firstPointerOffset == null)
                    {
                        firstPointerOffset = currentOffset + 2;
                    }

                    currentOffset = pointer;
                    continue;
                }

                currentOffset += 1;
                if (!EnsureAvailable(message, currentOffset, length))
                {
                    name = string.Empty;
                    return false;
                }

                labels.Add(System.Text.Encoding.ASCII.GetString(message, currentOffset, length));
                currentOffset += length;
            }

            offset = firstPointerOffset ?? currentOffset;
            name = string.Join(".", labels);
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
