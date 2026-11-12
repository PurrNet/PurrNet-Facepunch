using NUnit.Framework;
using PurrNet.Steam;
using UnityEngine;

// Steam is not running in CI, so this only proves the backend compiles against the bundled Facepunch.Steamworks
// and that SteamTransport picks it up. A compile error fails the run before any test starts.
public class FacepunchBackendTests
{
    [Test]
    public void BackendIsCompiledIntoSteamAddon()
    {
        var assembly = typeof(SteamTransport).Assembly;
        Assert.That(assembly.GetType("PurrNet.Steam.FacepunchServer"), Is.Not.Null, "FacepunchServer is missing");
        Assert.That(assembly.GetType("PurrNet.Steam.FacepunchClient"), Is.Not.Null, "FacepunchClient is missing");
    }

    [Test]
    public void SteamTransportIsSupported()
    {
        var go = new GameObject("SteamTransport");
        try
        {
            Assert.That(go.AddComponent<SteamTransport>().isSupported, Is.True);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
