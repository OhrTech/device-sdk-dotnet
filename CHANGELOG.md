# Changelog

## 0.1.0 (unreleased)

The first release: every area of the OhrTech Device SDK (net.ohrtech:device-sdk 0.3.0) for .NET for Android and MAUI
(`net10.0-android`), needing the OhrTech Agent 1059 or later (1061 for diagnostics, USB devices, certificates and the kiosk
exit PIN).

- `OhrTech.Device`: a static class per area with Task-returning calls, static events raised on the subscriber's
  synchronization context, `IAsyncEnumerable` streams, `Current` interfaces for dependency injection, and `OhrException`
  subclasses. The Kotlin library is bound internally. Trim- and NativeAOT-safe.
- `OhrWifiView`: the Agent's Wi-Fi screen as an Android view; `OhrTech.Device.Maui` has it as a MAUI control.
- Push handled by the app's handlers, whether the app is open or not.
- `OhrCertificates.CreateHttpHandler()`: an HttpClient handler with the app's client certificate and trusted CAs.
