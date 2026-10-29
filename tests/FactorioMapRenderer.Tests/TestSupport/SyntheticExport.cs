using System.Text;
using FactorioMapRenderer.Save;

namespace FactorioMapRenderer.Tests.TestSupport;

/// <summary>Writes what the export mod would write for a small world, and save headers as the game writes them.</summary>
internal static class SyntheticExport
{
    /// <summary>A folder with meta.json and one surface: grass with a lake, iron ore, a few buildings, a tag.</summary>
    public static string Write(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "meta.json"), """
            {"tick":216000,"ticks_played":216000,"mods":{"base":"2.0.32","factorio-map-renderer-export":"1.0.0"},
             "surfaces":[{"file":"surface-1.txt","name":"nauvis","index":1,"chunks":4,
                          "statistics":{"item":{"produced":{"iron-plate":1200,"iron-gear-wheel":300}},"fluid":{"produced":{"water":5000}}}}],
             "players":[{"name":"Engineer","index":1,"connected":true,"admin":true,"force":"player","online_time":216000,"surface":1,"x":3.5,"y":-2.5,"color":[222,128,33]}],
             "forces":[{"name":"player","index":1,"rockets_launched":2,"researched":10,"technologies":200,"research":"automation","research_progress":0.5},
                       {"name":"enemy","index":2},{"name":"neutral","index":3}]}
            """);

        var s = new StringBuilder();
        s.AppendLine("""S	1	{"name":"nauvis","index":1,"planet":"nauvis","seed":123456}""");
        s.AppendLine("T\t0\tgrass-1\t55\t83\t35");
        s.AppendLine("T\t1\twater\t51\t83\t95");
        // four charted chunks around the origin; the first has a lake in its top rows
        s.AppendLine("C\t-1\t-1\t1*96,0*928");
        s.AppendLine("C\t0\t-1\t0*1024");
        s.AppendLine("C\t-1\t0\t0*1024");
        s.AppendLine("C\t0\t0\t0*1024");
        s.AppendLine("""P	0	iron-ore	{"type":"resource","map":[105,133,147],"w":1,"h":1}""");
        s.AppendLine("""P	1	assembling-machine-1	{"type":"assembling-machine","friendly":[0,96,145],"enemy":[255,25,25],"w":3,"h":3}""");
        s.AppendLine("""P	2	transport-belt	{"type":"transport-belt","friendly":[204,160,71],"w":1,"h":1}""");
        s.AppendLine("""P	3	small-electric-pole	{"type":"electric-pole","friendly":[0,96,145],"w":1,"h":1}""");
        s.AppendLine("""P	4	tree-01	{"type":"tree","map":[48,99,48],"w":1,"h":1}""");
        s.AppendLine("""P	5	biter-spawner	{"type":"unit-spawner","enemy":[255,25,25],"w":5,"h":5}""");
        s.AppendLine("""P	6	train-stop	{"type":"train-stop","friendly":[0,96,145],"w":2,"h":2}""");
        for (int x = 10; x < 16; x++)
            for (int y = 10; y < 14; y++)
                s.AppendLine($"R\t0\t{x + 0.5}\t{y + 0.5}\t1500");
        s.AppendLine("""E	1	1	-6.35	-6.35	-3.65	-3.65	0	-5	-5	{"id":1,"status":"working","recipe":"iron-gear-wheel"}""");
        for (int x = -3; x < 5; x++)
            s.AppendLine($"E\t2\t1\t{x + 0.1}\t-4.4\t{x + 0.9}\t-3.6\t4\t{x + 0.5}\t-3.5");
        s.AppendLine("E\t3\t1\t-2.85\t-2.85\t-2.15\t-2.15\t0\t-2.5\t-2.5");
        s.AppendLine("E\t3\t1\t3.15\t-2.85\t3.85\t-2.15\t0\t3.5\t-2.5");
        s.AppendLine("W\t-2.5\t-2.5\t3.5\t-2.5");
        s.AppendLine("""E	5	2	20	20	25	25	0	22.5	22.5	{"force":"enemy"}""");
        s.AppendLine("""E	6	1	-20	5	-18	7	0	-19	6	{"id":7,"name":"Iron [item=iron-plate] pickup","limit":2}""");
        s.AppendLine("N\t4\t-20.5\t-20.5");
        s.AppendLine("""G	{"text":"Home","x":0,"y":0,"icon":{"type":"virtual","name":"signal-heart"},"user":"Engineer"}""");
        s.AppendLine("X\tsmall-biter\t12");
        File.WriteAllText(Path.Combine(directory, "surface-1.txt"), s.ToString());
        File.WriteAllText(Path.Combine(directory, "done"), "ok");
        return directory;
    }

    public static SaveHeader Header => new("test-world", new Version(2, 0, 32, 0), "freeplay", [new SaveMod("base", "2.0.32")]);

    /// <summary>The start of level.dat as Factorio 2.0 writes it.</summary>
    public static byte[] LevelHeader(ushort major, ushort minor, ushort patch, string level, params (string Name, byte Major, byte Minor, byte Patch)[] mods)
    {
        var b = new List<byte>();
        void U16(ushort v) => b.AddRange(BitConverter.GetBytes(v));
        void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            b.Add((byte)bytes.Length);
            b.AddRange(bytes);
        }
        U16(major); U16(minor); U16(patch); U16(0);
        b.Add(0);
        Str("");          // campaign
        Str(level);       // level
        Str("base");      // base mod
        // difficulty, finished, player won, next level, can continue, finished but continuing, saving replay,
        // allow non-admin debug options, loaded from (3 bytes), loaded from build (4 bytes), allowed commands, …
        b.AddRange([1, 0, 0, 0, 1, 0, 0, 1, (byte)major, (byte)minor, (byte)patch, 0xc5, 0x3d, 0x01, 0x00, 3, 0, 0, 0xa0, 0]);
        b.Add((byte)mods.Length);
        foreach (var (name, ma, mi, pa) in mods)
        {
            Str(name);
            b.AddRange([ma, mi, pa, 1, 2, 3, 4]);
        }
        b.AddRange(new byte[64]);
        return [.. b];
    }
}
