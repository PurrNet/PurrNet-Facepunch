# PurrNet Facepunch

Lets PurrNet's `SteamTransport` run on [Facepunch.Steamworks](https://github.com/Facepunch/Facepunch.Steamworks) instead of Steamworks.NET.

This package includes Facepunch.Steamworks 2.5.2 and the Steam native libraries, so there is nothing else to install.

## Install

Select a `SteamTransport` in a project that has no Steam library yet and press **Add Facepunch.Steamworks to Package Manager**.

To add it by hand, open Unity's Package Manager, click **Add package from git URL**, and paste one of these.

Stable:

```
https://github.com/PurrNet/PurrNet-Facepunch.git?path=/Assets/PurrNetFacepunch#release
```

Latest in-development:

```
https://github.com/PurrNet/PurrNet-Facepunch.git?path=/Assets/PurrNetFacepunch#dev
```

- Requires PurrNet.
- Do not install it next to Steamworks.NET. Both libraries use the `Steamworks` namespace.
- If Facepunch.Steamworks is already in your `Assets` folder, delete that copy first.

## Use

Initialize Steam yourself, for example `Steamworks.SteamClient.Init(appId)`, then use `SteamTransport` as usual.

Differences from the Steamworks.NET backend:

- Send rate and send buffer size apply to every Steam connection in the process, not per connection.
- The dedicated server toggle has no effect. Facepunch.Steamworks picks the client or game server interface itself.
- A connection closed by a failed send always reports `Timeout`.

## License

MIT. See [`LICENSE`](./LICENSE). Facepunch.Steamworks is MIT licensed by Facepunch Studios; its licence is in `Assets/PurrNetFacepunch/Plugins`.
