using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TouchNStars.Server.Services;

/// <summary>
/// Spatial coverage of a HiPS survey read from its <c>Moc.fits</c> (IVOA MOC, NUNIQ
/// encoding in a FITS binary table). Answers which tiles of a given order exist, so a survey
/// that covers only part of the sky is neither downloaded nor counted as incomplete outside
/// its footprint.
/// </summary>
public sealed class MocCoverage
{
    private const int FitsBlockSize = 2880;
    private const int FitsCardSize = 80;

    private readonly List<(int Order, long Npix)> cells;
    private readonly Dictionary<int, int[]> tilesByOrder = new();

    private MocCoverage(List<(int Order, long Npix)> cells)
    {
        this.cells = cells;
    }

    public int CellCount => cells.Count;

    /// <summary>Sorted indices of every tile of <paramref name="order"/> touching the coverage.</summary>
    public IReadOnlyList<int> Tiles(int order)
    {
        lock (tilesByOrder)
        {
            if (!tilesByOrder.TryGetValue(order, out int[] tiles))
            {
                tiles = ComputeTiles(order);
                tilesByOrder[order] = tiles;
            }

            return tiles;
        }
    }

    public int TileCount(int order) => Tiles(order).Count;

    private int[] ComputeTiles(int order)
    {
        HashSet<int> tiles = new();
        foreach ((int cellOrder, long npix) in cells)
        {
            if (cellOrder <= order)
            {
                int shift = 2 * (order - cellOrder);
                long first = npix << shift;
                long end = (npix + 1) << shift;
                for (long tile = first; tile < end; tile++)
                {
                    tiles.Add((int)tile);
                }
            }
            else
            {
                tiles.Add((int)(npix >> (2 * (cellOrder - order))));
            }
        }

        int[] sorted = tiles.ToArray();
        Array.Sort(sorted);
        return sorted;
    }

    /// <summary>Decodes a NUNIQ value into its HEALPix order and nested pixel index.</summary>
    public static (int Order, long Npix) DecodeNuniq(long nuniq)
    {
        if (nuniq < 4)
        {
            throw new FormatException($"Invalid NUNIQ value {nuniq}.");
        }

        int order = 0;
        while (4L << (2 * (order + 1)) <= nuniq)
        {
            order++;
        }

        return (order, nuniq - (4L << (2 * order)));
    }

    /// <summary>
    /// Parses a MOC FITS file: primary HDU without data, then a BINTABLE with one NUNIQ
    /// column of 32-bit (1J) or 64-bit (1K) big-endian integers.
    /// </summary>
    public static MocCoverage Parse(byte[] fits)
    {
        if (fits == null || fits.Length < FitsBlockSize)
        {
            throw new FormatException("MOC file is too short.");
        }

        int offset = 0;
        Dictionary<string, string> primary = ReadHeader(fits, ref offset);
        if (!primary.TryGetValue("SIMPLE", out string simple) || simple != "T")
        {
            throw new FormatException("MOC file is not FITS.");
        }

        offset += DataSize(primary);
        Dictionary<string, string> table = ReadHeader(fits, ref offset);
        if (!table.TryGetValue("XTENSION", out string extension) || extension != "BINTABLE")
        {
            throw new FormatException("MOC file has no binary table.");
        }

        if (table.TryGetValue("ORDERING", out string ordering) && ordering != "NUNIQ")
        {
            throw new FormatException($"Unsupported MOC ordering '{ordering}'.");
        }

        int rowBytes = ReadInt(table, "NAXIS1");
        int rows = ReadInt(table, "NAXIS2");
        string form = table.TryGetValue("TFORM1", out string f) ? f : string.Empty;
        int valueBytes = form.EndsWith('K') ? 8 : form.EndsWith('J') ? 4 : 0;
        if (valueBytes == 0 || rowBytes < valueBytes)
        {
            throw new FormatException($"Unsupported MOC column format '{form}'.");
        }

        if (offset + (long)rowBytes * rows > fits.Length)
        {
            throw new FormatException("MOC file is truncated.");
        }

        List<(int, long)> cells = new(rows);
        for (int row = 0; row < rows; row++)
        {
            int start = offset + row * rowBytes;
            long value = 0;
            for (int i = 0; i < valueBytes; i++)
            {
                value = (value << 8) | fits[start + i];
            }

            if (valueBytes == 4)
            {
                value = (int)value;
            }

            cells.Add(DecodeNuniq(value));
        }

        if (cells.Count == 0)
        {
            throw new FormatException("MOC file lists no cells.");
        }

        return new MocCoverage(cells);
    }

    private static Dictionary<string, string> ReadHeader(byte[] fits, ref int offset)
    {
        Dictionary<string, string> header = new(StringComparer.Ordinal);
        while (offset + FitsCardSize <= fits.Length)
        {
            string card = Encoding.ASCII.GetString(fits, offset, FitsCardSize);
            offset += FitsCardSize;
            string key = card[..8].Trim();
            if (key == "END")
            {
                offset = (offset + FitsBlockSize - 1) / FitsBlockSize * FitsBlockSize;
                return header;
            }

            if (card.Length > 10 && card[8] == '=')
            {
                string value = card[10..].Trim();
                if (value.StartsWith('\''))
                {
                    // Quoted string: the value ends at the closing quote, a comment may follow.
                    int closing = value.IndexOf('\'', 1);
                    value = closing > 0 ? value[1..closing] : value[1..];
                }
                else
                {
                    int comment = value.IndexOf('/');
                    if (comment >= 0)
                    {
                        value = value[..comment];
                    }
                }

                header[key] = value.Trim();
            }
        }

        throw new FormatException("FITS header has no END card.");
    }

    private static int DataSize(Dictionary<string, string> header)
    {
        int axes = header.TryGetValue("NAXIS", out string n) ? int.Parse(n, CultureInfo.InvariantCulture) : 0;
        if (axes == 0)
        {
            return 0;
        }

        long bytes = Math.Abs(ReadInt(header, "BITPIX")) / 8;
        for (int axis = 1; axis <= axes; axis++)
        {
            bytes *= ReadInt(header, $"NAXIS{axis}");
        }

        return (int)((bytes + FitsBlockSize - 1) / FitsBlockSize * FitsBlockSize);
    }

    private static int ReadInt(Dictionary<string, string> header, string key)
    {
        if (header.TryGetValue(key, out string raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        throw new FormatException($"FITS header lacks '{key}'.");
    }
}
