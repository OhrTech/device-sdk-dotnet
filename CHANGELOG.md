# Changelog

## 0.1.1 (2026-11-10)

Builds on net.ohrtech:device-sdk 0.3.2.

- A push message that doesn't reach your handler is no longer lost silently: the hub shows it as dropped, and its id is forgotten, so the sender may send it again (before, it was logged and counted as delivered). The reason says why:
  `app_no_handler` (`OhrPush.SetHandlers` or `UseOhrTechDevice` with `OnPushMessage` never ran), `app_handler_timeout`
  (over 25 s) or `app_handler` (the handler threw).
- `OhrPushSuite.WireName` ("x25519", "xwing-mldsa65") and `OhrPushSuiteNames.FromWireName`; `OhrKeyAlgorithm.StandardName`
  ("ML-KEM-768", "ML-KEM-1024", "X-Wing", "X25519") and `OhrKeyAlgorithmNames.FromStandardName`, as the Management API
  names them.
- `OhrCertificates.CreateHttpHandler` drops its cached TLS setup when the fleet changes the app's certificates (trusted
  CAs, bundles), not only after `RequestAsync` or a renewal: with Agent 1061 or later, such a change applies to the
  next request.
- A MAUI sample (samples/OhrTech.Device.Maui.Sample): `UseOhrTechDevice()` with the push handlers, the Wi-Fi screen in
  XAML, and areas injected into the page.

## 0.1.0 (2026-11-09)

The first release: every area of the OhrTech Device SDK (net.ohrtech:device-sdk 0.3.1) for .NET for Android and MAUI
(`net10.0-android`), needing the OhrTech Agent 1059 or later (1061 for diagnostics, USB devices, certificates and the kiosk
exit PIN).

- `OhrTech.Device`: a static class per area with Task-returning calls, static events raised on the subscriber's
  synchronization context, `IAsyncEnumerable` streams, `Current` interfaces for dependency injection, and `OhrException`
  subclasses. The Kotlin library is bound internally. Trim- and NativeAOT-safe.
- `OhrWifiView`: the Agent's Wi-Fi screen as an Android view; `OhrTech.Device.Maui` has it as a MAUI control.
- Push handled by the app's handlers, whether the app is open or not.
- `OhrCertificates.CreateHttpHandler()`: an HttpClient handler with the app's client certificate and trusted CAs.
