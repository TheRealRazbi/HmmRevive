using System;
using System.IO;
using System.Text;
using HmmRevive.Launcher;
using Xunit;

namespace HmmRevive.Launcher.Tests
{
    public class ServerClientsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hmmrevive-tests-" + Guid.NewGuid().ToString("N"));
        public ServerClientsTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string Log(string text)
        {
            string p = Path.Combine(_dir, "hmmrevive-server-9696.log");
            File.WriteAllText(p, text);
            return p;
        }

        [Fact]
        public void TheLastLineWinsWhenThereAreSeveral()
        {
            string p = Log("[1] WATCH server:clients=0 x\n[2] other\n[3] WATCH server:clients=3 x\n[4] WATCH server:clients=2 x\n[5] noise\n");
            Assert.Equal(2, Game.ClientsInLog(p));
        }

        [Fact]
        public void AnOnlyLineIsRead() => Assert.Equal(5, Game.ClientsInLog(Log("WATCH server:clients=5")));

        [Fact]
        public void AnEmptyLogIsZero() => Assert.Equal(0, Game.ClientsInLog(Log("")));

        [Fact]
        public void AMissingLogIsZeroAndDoesNotThrow() => Assert.Equal(0, Game.ClientsInLog(Path.Combine(_dir, "nope.log")));

        [Fact]
        public void OnlyTheFinal64KibAreReadAndALogBeingWrittenIsNotLocked()
        {
            string p = Path.Combine(_dir, "hmmrevive-server-9696.log");
            using (var w = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                byte[] old = Encoding.UTF8.GetBytes("WATCH server:clients=7\n");
                w.Write(old, 0, old.Length);
                byte[] filler = Encoding.UTF8.GetBytes(new string('x', 80 * 1024) + "\n");
                w.Write(filler, 0, filler.Length);
                w.Flush();
                Assert.Equal(0, Game.ClientsInLog(p)); // the 7 is before the last 64 KiB, and the file is open for writing
                byte[] last = Encoding.UTF8.GetBytes("WATCH server:clients=4\n");
                w.Write(last, 0, last.Length);
                w.Flush();
                Assert.Equal(4, Game.ClientsInLog(p));
            }
        }
    }
}
