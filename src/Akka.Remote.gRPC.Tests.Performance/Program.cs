using System;
using NBench;

namespace Akka.Remote.gRPC.Tests.Performance
{
    class Program
    {
        static int Main(string[] args)
        {
            return NBenchRunner.Run<Program>();
        }
    }
}
