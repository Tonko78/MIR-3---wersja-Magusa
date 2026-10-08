using System;
using System.IO;

namespace Server.AccountQueue;

/// <summary>
/// The web service is intended to write only to <see cref="Incoming"/>.
/// ServerCore exclusively owns processing, results, archives, quarantine, and duplicates.
/// </summary>
public sealed class QueuePaths
{
    public QueuePaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("The account queue root path is required.", nameof(root));
        }

        Root = Path.GetFullPath(root);
        Incoming = Path.Combine(Root, "incoming");
        Processing = Path.Combine(Root, "processing");
        Results = Path.Combine(Root, "results");
        Processed = Path.Combine(Root, "processed");
        Rejected = Path.Combine(Root, "rejected");
        Uncertain = Path.Combine(Root, "uncertain");
        Duplicates = Path.Combine(Root, "duplicates");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Incoming);
        Directory.CreateDirectory(Processing);
        Directory.CreateDirectory(Results);
        Directory.CreateDirectory(Processed);
        Directory.CreateDirectory(Rejected);
        Directory.CreateDirectory(Uncertain);
        Directory.CreateDirectory(Duplicates);
    }

    public string Root { get; }
    public string Incoming { get; }
    public string Processing { get; }
    public string Results { get; }
    public string Processed { get; }
    public string Rejected { get; }
    public string Uncertain { get; }
    public string Duplicates { get; }
}
