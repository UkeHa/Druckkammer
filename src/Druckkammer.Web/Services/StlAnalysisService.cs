using System.Globalization;
using System.Numerics;
using System.Text;
using Druckkammer.Web.Data.Entities;

namespace Druckkammer.Web.Services;

public record MeshAnalysisResult(
    double VolumeCm3,
    double DimXMm,
    double DimYMm,
    double DimZMm,
    int TriangleCount,
    bool IsValid);

public class StlAnalysisService
{
    /// <summary>
    /// Analysiert eine STL- oder OBJ-Datei (Binär oder ASCII) und berechnet Volumen (in cm³ = ml) sowie Bounding-Box (in mm).
    /// </summary>
    public MeshAnalysisResult AnalyzeFile(string filePath)
    {
        if (!File.Exists(filePath))
            return new MeshAnalysisResult(0, 0, 0, 0, 0, false);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        using var fs = File.OpenRead(filePath);
        if (ext == ".obj")
            return AnalyzeObjStream(fs);

        return AnalyzeStlStream(fs);
    }

    public MeshAnalysisResult AnalyzeStlStream(Stream stream)
    {
        if (stream.Length < 84)
        {
            return AnalyzeAsciiStlStream(stream);
        }

        // Check if binary STL matches expected byte length: 80 header + 4 count + count * 50 bytes
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var headerBytes = reader.ReadBytes(80);
        uint triangleCount = reader.ReadUInt32();

        long expectedLength = 84L + (long)triangleCount * 50L;
        string headerText = Encoding.ASCII.GetString(headerBytes);

        // If header starts with "solid" AND expected binary length doesn't match stream length, parse as ASCII
        if (headerText.StartsWith("solid", StringComparison.OrdinalIgnoreCase) && stream.Length != expectedLength)
        {
            stream.Position = 0;
            return AnalyzeAsciiStlStream(stream);
        }

        if (triangleCount == 0 || expectedLength > stream.Length + 50)
        {
            stream.Position = 0;
            return AnalyzeAsciiStlStream(stream);
        }

        double signedVolumeMm3 = 0.0;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        int actualTriangles = 0;

        for (uint i = 0; i < triangleCount && stream.Position + 50 <= stream.Length; i++)
        {
            // Skip normal vector (3 floats = 12 bytes)
            _ = reader.ReadSingle();
            _ = reader.ReadSingle();
            _ = reader.ReadSingle();

            var v1 = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var v2 = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var v3 = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            // Attribute byte count (2 bytes)
            _ = reader.ReadUInt16();

            AccumulateTriangle(v1, v2, v3, ref signedVolumeMm3, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            actualTriangles++;
        }

        if (actualTriangles == 0)
            return new MeshAnalysisResult(0, 0, 0, 0, 0, false);

        double volumeCm3 = Math.Abs(signedVolumeMm3) / 1000.0;
        double dimX = Math.Max(0, maxX - minX);
        double dimY = Math.Max(0, maxY - minY);
        double dimZ = Math.Max(0, maxZ - minZ);

        return new MeshAnalysisResult(
            Math.Round(volumeCm3, 2),
            Math.Round(dimX, 1),
            Math.Round(dimY, 1),
            Math.Round(dimZ, 1),
            actualTriangles,
            true);
    }

    public MeshAnalysisResult AnalyzeAsciiStlStream(Stream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);

        double signedVolumeMm3 = 0.0;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        int triangleCount = 0;

        Vector3[] currentVertices = new Vector3[3];
        int vertexIdx = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed[6..].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    currentVertices[vertexIdx++] = new Vector3(x, y, z);
                    if (vertexIdx == 3)
                    {
                        AccumulateTriangle(currentVertices[0], currentVertices[1], currentVertices[2],
                            ref signedVolumeMm3, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
                        triangleCount++;
                        vertexIdx = 0;
                    }
                }
            }
        }

        if (triangleCount == 0)
            return new MeshAnalysisResult(0, 0, 0, 0, 0, false);

        double volumeCm3 = Math.Abs(signedVolumeMm3) / 1000.0;
        return new MeshAnalysisResult(
            Math.Round(volumeCm3, 2),
            Math.Round(Math.Max(0, maxX - minX), 1),
            Math.Round(Math.Max(0, maxY - minY), 1),
            Math.Round(Math.Max(0, maxZ - minZ), 1),
            triangleCount,
            true);
    }

    public MeshAnalysisResult AnalyzeObjStream(Stream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);

        var vertices = new List<Vector3>();
        double signedVolumeMm3 = 0.0;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        int triangleCount = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("v "))
            {
                var parts = trimmed[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    vertices.Add(new Vector3(x, y, z));
                }
            }
            else if (trimmed.StartsWith("f "))
            {
                var parts = trimmed[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var faceIndices = new List<int>();
                    foreach (var p in parts)
                    {
                        var idxStr = p.Split('/')[0];
                        if (int.TryParse(idxStr, out int idx) && idx > 0 && idx <= vertices.Count)
                            faceIndices.Add(idx - 1);
                    }
                    for (int i = 1; i + 1 < faceIndices.Count; i++)
                    {
                        AccumulateTriangle(vertices[faceIndices[0]], vertices[faceIndices[i]], vertices[faceIndices[i + 1]],
                            ref signedVolumeMm3, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
                        triangleCount++;
                    }
                }
            }
        }

        if (triangleCount == 0)
            return new MeshAnalysisResult(0, 0, 0, 0, 0, false);

        return new MeshAnalysisResult(
            Math.Round(Math.Abs(signedVolumeMm3) / 1000.0, 2),
            Math.Round(Math.Max(0, maxX - minX), 1),
            Math.Round(Math.Max(0, maxY - minY), 1),
            Math.Round(Math.Max(0, maxZ - minZ), 1),
            triangleCount,
            true);
    }

    private static void AccumulateTriangle(
        Vector3 v1, Vector3 v2, Vector3 v3,
        ref double signedVolumeMm3,
        ref float minX, ref float minY, ref float minZ,
        ref float maxX, ref float maxY, ref float maxZ)
    {
        // Signed volume of tetrahedron with origin: (v1 . (v2 x v3)) / 6.0
        var cross = Vector3.Cross(v2, v3);
        signedVolumeMm3 += Vector3.Dot(v1, cross) / 6.0;

        minX = Math.Min(minX, Math.Min(v1.X, Math.Min(v2.X, v3.X)));
        minY = Math.Min(minY, Math.Min(v1.Y, Math.Min(v2.Y, v3.Y)));
        minZ = Math.Min(minZ, Math.Min(v1.Z, Math.Min(v2.Z, v3.Z)));

        maxX = Math.Max(maxX, Math.Max(v1.X, Math.Max(v2.X, v3.X)));
        maxY = Math.Max(maxY, Math.Max(v1.Y, Math.Max(v2.Y, v3.Y)));
        maxZ = Math.Max(maxZ, Math.Max(v1.Z, Math.Max(v2.Z, v3.Z)));
    }

    /// <summary>
    /// Berechnet das skalierte Gesamtvolumen (inkl. Support-/Waschverlust-Aufschlag) in ml/cm³.
    /// Das Volumen skaliert kubisch mit dem Maßstabsfaktor (s/100)³.
    /// </summary>
    public static double CalculateEstimatedTotalVolumeMl(
        double baseUnitVolumeCm3,
        int quantity,
        double scalePercentage = 100.0,
        double supportMultiplier = 1.25)
    {
        if (baseUnitVolumeCm3 <= 0 || quantity <= 0 || scalePercentage <= 0)
            return 0;

        double scaleRatio = scalePercentage / 100.0;
        double cubicScale = scaleRatio * scaleRatio * scaleRatio;
        return Math.Round(baseUnitVolumeCm3 * cubicScale * quantity * supportMultiplier, 2);
    }

    /// <summary>
    /// Berechnet die geschätzten Materialkosten in EUR anhand des Gesamtvolumens und Materialpreises pro Liter/kg.
    /// </summary>
    public static decimal CalculateEstimatedMaterialCostEur(
        double estimatedTotalVolumeMl,
        decimal pricePerLiterOrKg,
        double densityGPerCm3 = 1.15,
        PrintingTechnology tech = PrintingTechnology.ResinSLA)
    {
        if (estimatedTotalVolumeMl <= 0 || pricePerLiterOrKg <= 0)
            return 0m;

        if (tech == PrintingTechnology.FilamentFDM)
        {
            double weightKg = (estimatedTotalVolumeMl * densityGPerCm3) / 1000.0;
            return Math.Round((decimal)weightKg * pricePerLiterOrKg, 2);
        }

        // Resin wird in Litern (1000 ml) abgerechnet
        double liters = estimatedTotalVolumeMl / 1000.0;
        return Math.Round((decimal)liters * pricePerLiterOrKg, 2);
    }

    /// <summary>
    /// Maßstabs-Umrechner: Berechnet den Skalierungsfaktor in Prozent von einem Quell-Maßstab (z.B. 28mm) auf einen Ziel-Maßstab (z.B. 32mm).
    /// </summary>
    public static double ConvertTabletopScalePercentage(double fromScaleMm, double toScaleMm)
    {
        if (fromScaleMm <= 0 || toScaleMm <= 0)
            return 100.0;
        return Math.Round((toScaleMm / fromScaleMm) * 100.0, 1);
    }

    /// <summary>
    /// Berechnet den Skalierungsfaktor in Prozent anhand der Originalhöhe (Bounding Box Z) und einer gewünschten Zielhöhe in mm.
    /// </summary>
    public static double CalculateScaleFromTargetHeightPercentage(double nativeHeightMm, double targetHeightMm)
    {
        if (nativeHeightMm <= 0 || targetHeightMm <= 0)
            return 100.0;
        return Math.Round((targetHeightMm / nativeHeightMm) * 100.0, 1);
    }

    /// <summary>
    /// Schlägt anhand der Bounding-Box automatisch eine Tabletop-Kreaturengröße vor.
    /// </summary>
    public static TabletopScaleSize SuggestTabletopSize(double dimXMm, double dimYMm, double dimZMm)
    {
        double footprint = Math.Max(dimXMm, dimYMm);
        if (footprint <= 0 && dimZMm <= 0)
            return TabletopScaleSize.Unknown;
        if (footprint < 20 && dimZMm < 25)
            return TabletopScaleSize.Tiny;
        if (footprint <= 38 && dimZMm <= 52)
            return TabletopScaleSize.SmallMedium;
        if (footprint <= 60 && dimZMm <= 80)
            return TabletopScaleSize.Large;
        if (footprint <= 85 && dimZMm <= 120)
            return TabletopScaleSize.Huge;
        return TabletopScaleSize.Gargantuan;
    }

    /// <summary>
    /// Erzeugt eine valide binäre STL-Datei (Miniatur-Sockel + Kristall-Obelisk/Figur) für Demo-Modelle und Tests.
    /// </summary>
    public static byte[] GenerateSampleBinaryStl(float baseRadiusMm = 16f, float totalHeightMm = 38f, int segments = 12)
    {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();

        float baseHeight = Math.Min(4.0f, totalHeightMm * 0.12f);
        float pillarRadiusBottom = baseRadiusMm * 0.55f;
        float pillarRadiusTop = baseRadiusMm * 0.28f;
        float bodyTopZ = totalHeightMm * 0.78f;

        // 1. Base cylinder (Z: 0 -> baseHeight)
        AddFrustumTriangles(triangles, baseRadiusMm, baseRadiusMm * 0.92f, 0f, baseHeight, segments, closeBottom: true, closeTop: true);

        // 2. Main figure/obelisk body (Z: baseHeight -> bodyTopZ)
        AddFrustumTriangles(triangles, pillarRadiusBottom, pillarRadiusTop, baseHeight, bodyTopZ, segments, closeBottom: false, closeTop: false);

        // 3. Spire/head tip (Z: bodyTopZ -> totalHeightMm)
        AddFrustumTriangles(triangles, pillarRadiusTop, 0.0f, bodyTopZ, totalHeightMm, segments, closeBottom: false, closeTop: false);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        byte[] header = new byte[80];
        Encoding.ASCII.GetBytes("Druckkammer Generated Binary STL Mesh").CopyTo(header, 0);
        writer.Write(header);
        writer.Write((uint)triangles.Count);

        foreach (var (a, b, c) in triangles)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (float.IsNaN(normal.X)) normal = Vector3.UnitZ;

            writer.Write(normal.X); writer.Write(normal.Y); writer.Write(normal.Z);
            writer.Write(a.X); writer.Write(a.Y); writer.Write(a.Z);
            writer.Write(b.X); writer.Write(b.Y); writer.Write(b.Z);
            writer.Write(c.X); writer.Write(c.Y); writer.Write(c.Z);
            writer.Write((ushort)0);
        }

        writer.Flush();
        return ms.ToArray();
    }

    private static void AddFrustumTriangles(
        List<(Vector3 A, Vector3 B, Vector3 C)> triangles,
        float rBottom, float rTop, float zBottom, float zTop, int segments,
        bool closeBottom, bool closeTop)
    {
        for (int i = 0; i < segments; i++)
        {
            double a0 = 2.0 * Math.PI * i / segments;
            double a1 = 2.0 * Math.PI * (i + 1) / segments;

            var b0 = new Vector3((float)(rBottom * Math.Cos(a0)), (float)(rBottom * Math.Sin(a0)), zBottom);
            var b1 = new Vector3((float)(rBottom * Math.Cos(a1)), (float)(rBottom * Math.Sin(a1)), zBottom);
            var t0 = new Vector3((float)(rTop * Math.Cos(a0)), (float)(rTop * Math.Sin(a0)), zTop);
            var t1 = new Vector3((float)(rTop * Math.Cos(a1)), (float)(rTop * Math.Sin(a1)), zTop);

            if (closeBottom)
            {
                var centerBottom = new Vector3(0, 0, zBottom);
                triangles.Add((centerBottom, b1, b0));
            }

            if (rTop > 0.001f)
            {
                triangles.Add((b0, b1, t1));
                triangles.Add((b0, t1, t0));
                if (closeTop)
                {
                    var centerTop = new Vector3(0, 0, zTop);
                    triangles.Add((centerTop, t0, t1));
                }
            }
            else
            {
                var apex = new Vector3(0, 0, zTop);
                triangles.Add((b0, b1, apex));
            }
        }
    }
}
