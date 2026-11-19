# LC-9 · Original identity and encrypted legacy connections

On 19 November 2026 the owner selected original-compatible player identity and encrypted connections when a Vortex client joins a DarkPlaces server. This extends the first-version scope in [ADR-0019](../decisions/ADR-0019-legacy-compatibility-mode.md). Native Vortex sessions use the authentication boundary in [ADR-0011](../decisions/ADR-0011-protocol-ecosystem-boundary.md). LC-9 is planned work.

## Reference and delivery order

Use Xonotic data `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` and DarkPlaces `d93f9c4292039354a2b8d40d11bc386891e55fe5`. DarkPlaces `crypto.c` owns identity loading, negotiation, encryption and peer fingerprints. Its default `crypto_aeslevel` is 1. The stock server-info dialog reads the negotiated or cached result through the menu builtins.

1. **Package the original providers.** Prototype a small binding to the upstream d0 identity and Rijndael libraries in the legacy assembly. Pin versions, verify licensing and dependencies for the exported desktop platforms, and exercise library discovery in clean exports. Expose actual provider availability to the legacy extension queries.
2. **Establish identity continuity.** Support explicitly importing a player's existing Xonotic identity or generating one in the legacy user profile. Load the original public key slots and private identity format. Exercise the original signed/unsigned identity paths against a local test authority. Keep secret identity data in user-private storage with atomic writes. Display the public fingerprint and verify it survives reconnect and restart.
3. **Negotiate on the legacy connection.** Integrate the original connectionless crypto state machine with `DpConnectionHandshake`. Today it accepts the challenge text and discards the binary extension. Bind negotiation state to the current peer, retire it on reconnect or timeout, and enforce the requested encryption level before handing a channel to `DpClient`.
4. **Protect connected packets.** Apply the equivalents of `Crypto_EncryptPacket` and `Crypto_DecryptPacket` at the legacy datagram boundary. Check packet size budgets, reliable resends, signon, CSQC download, input, map changes and disconnect over a real authenticated channel. Report an actionable connection failure when encryption is required and negotiation cannot complete.
5. **Expose verified state.** Implement the identity, host-key, signature-status and encryption-level builtins in `MenuHost.Builtins.cs` from live or explicitly cached peer data. Advertise extension support once the corresponding operations work. Confirm the reference server sees a stable player fingerprint across sessions. Verify stats attribution locally with a test endpoint before considering any external integration.

## Interoperability acceptance

| Scenario | Observable acceptance criterion |
|---|---|
| Existing signed identity | Original client and legacy Vortex report the same public identity fingerprint when using an explicitly imported test identity. |
| Generated or unsigned identity | Status matches original behavior. A later restart preserves the identity and private material stays out of logs and recordings. |
| Policy negotiation | Run both clients against reference servers at each `crypto_aeslevel` from 0 through 3, for each client level. Compare connection result, authenticated identity and encryption status. |
| Required encryption | A successful join transfers the client program and plays a match over the encrypted channel. An incompatible peer fails without a plaintext retry. |
| Failure and recovery | Rejected credentials, a failed packet check, lost negotiation packets and reconnects produce the expected reference outcome. The next connection starts with fresh peer state. |
| Native regression | Native authentication and protocol suites still pass with the legacy providers present and absent. |
