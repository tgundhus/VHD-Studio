using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VhdAttachCommon;

namespace VhdAttachTest {

    /// <summary>
    /// Opt-in performance comparison of the verified backup against a plain file copy.
    /// Set VHDSTUDIO_BENCH=1 (and optionally VHDSTUDIO_BENCH_GB) to run.
    /// </summary>
    [TestClass()]
    public class BackupBenchmarkTest {

        [TestMethod()]
        [TestCategory("Benchmark")]
        public void Benchmark_BackupVersusPlainCopy() {
            if (Environment.GetEnvironmentVariable("VHDSTUDIO_BENCH") != "1") { Assert.Inconclusive("Set VHDSTUDIO_BENCH=1 to run the benchmark."); }
            var gigabytes = int.TryParse(Environment.GetEnvironmentVariable("VHDSTUDIO_BENCH_GB"), out var gb) ? gb : 3;
            var folder = Path.Combine(Path.GetTempPath(), "VhdStudioBench");
            Directory.CreateDirectory(folder);
            var source = Path.Combine(folder, "source.bin");
            var plain = Path.Combine(folder, "plain.bin");
            var backup = Path.Combine(folder, "backup.bin");
            try {
                using (var stream = new FileStream(source, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)) {
                    var buffer = new byte[16 << 20];
                    RandomNumberGenerator.Fill(buffer);
                    for (long written = 0; written < (long)gigabytes << 30; written += buffer.Length) {
                        buffer[0]++; //distinct blocks, no compression or dedup shortcuts
                        stream.Write(buffer, 0, buffer.Length);
                    }
                }

                var copy = Stopwatch.StartNew();
                File.Copy(source, plain);
                copy.Stop();
                var durable = Stopwatch.StartNew(); //what the copy costs once the data is actually on disk
                using (var flush = new FileStream(plain, FileMode.Open, FileAccess.ReadWrite)) { flush.Flush(true); }
                durable.Stop();
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "VhdStudioDurable.txt"), string.Format("File.Copy + flush to disk: {0:N1} s", copy.Elapsed.TotalSeconds + durable.Elapsed.TotalSeconds));

                var verified = Stopwatch.StartNew();
                VirtualDiskBackup.Create(source, backup, null, CancellationToken.None);
                verified.Stop();

                var memory = new byte[1 << 30];
                RandomNumberGenerator.Fill(memory);
                var hashing = Stopwatch.StartNew();
                SHA256.HashData(memory);
                hashing.Stop();
                Console.WriteLine("SHA-256 in memory: {0:N0} MB/s", 1024 / hashing.Elapsed.TotalSeconds);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "VhdStudioHash.txt"), string.Format("SHA-256 in memory: {0:N0} MB/s", 1024 / hashing.Elapsed.TotalSeconds));

                var size = new FileInfo(source).Length / 1048576.0;
                var message = string.Format("{0} GB: File.Copy {1:N1} s ({2:N0} MB/s); verified backup {3:N1} s ({4:N0} MB/s, {5:N2}x copy time)",
                    gigabytes, copy.Elapsed.TotalSeconds, size / copy.Elapsed.TotalSeconds, verified.Elapsed.TotalSeconds, size / verified.Elapsed.TotalSeconds, verified.Elapsed.TotalSeconds / copy.Elapsed.TotalSeconds);
                message += string.Format(" [copy {0:N1} s, flush {1:N1} s, verify {2:N1} s]", VirtualDiskBackup.LastTimings.Copy.TotalSeconds, VirtualDiskBackup.LastTimings.Flush.TotalSeconds, VirtualDiskBackup.LastTimings.Verify.TotalSeconds);
                Console.WriteLine(message);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "VhdStudioBench.txt"), message);
            } finally {
                Directory.Delete(folder, true);
            }
        }

    }
}
