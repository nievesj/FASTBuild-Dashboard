using System.IO;
using System.Net;

namespace FastBuild.Dashboard.Services.RemoteWorker;

internal class RemoteWorkerAgent : IRemoteWorkerAgent
{
    private RemoteWorkerAgent()
    {
    }

    public string FilePath { get; private set; }
    public bool IsLocal { get; private set; }
    public string Version { get; private set; }
    public string User { get; private set; }
    public string HostName { get; private set; }
    public string IPv4Address { get; private set; }
    public string DomainName { get; private set; }
    public string FQDN { get; private set; }
    public string CPUs { get; private set; }
    public string CPUDetails { get; private set; }
    public string Memory { get; private set; }
    public string Mode { get; private set; }

    public static RemoteWorkerAgent CreateFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var worker = new RemoteWorkerAgent();
        worker.FilePath = filePath;

        try
        {
            foreach (var line in File.ReadAllLines(filePath))
            {
                var propertyName = "";
                var propertyValue = "";

                try
                {
                    var data = line.Split(':');
                    if ( data.Length < 2 )
                    {
                        continue; // Not a property line (e.g. blank)
                    }
                    propertyName = data[ 0 ].Trim().Replace( " ", "" );
                    propertyValue = data[ 1 ].Trim();

                    var property = typeof( RemoteWorkerAgent ).GetProperty( propertyName );
                    if ( property == null )
                    {
                        // Unknown property (e.g. written by a newer worker build) -
                        // skip the line instead of discarding the whole worker.
                        continue;
                    }
                    property.SetValue( worker, propertyValue );
                }
                catch
                {
                    continue; // Malformed line - skip rather than discarding the worker
                }
            }
        }
        catch
        {
            //Console.WriteLine($"WARNING: {filePath} is not valid.");
            return null;
        }

        if ( string.Equals( worker.HostName, Dns.GetHostName(), System.StringComparison.OrdinalIgnoreCase ) )
            worker.IsLocal = true;
        else
            worker.IsLocal = false;

        return worker;
    }
}