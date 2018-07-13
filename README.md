# Yllibed Multiplexing Encapsulated Streaming Protocol (YMESP)

The _Multiplexing_ is a mecanism to establish _inner streams_, multiplexed
into the main stream (called _low-level stream_).

> This protocol is in no way part of any standard and has been designed by Carl de Billy in July 2017.
> The name _Yllibed_ is from his last name, once reversed.

## Installing

Get it on Nuget:

[![NuGet](https://buildstats.info/nuget/Yllibed.StreamMultiplexer?includePreReleases=false)](https://www.nuget.org/packages/Yllibed.StreamMultiplexer/)

## Features
 * **Peers are equals**: There is no "server" or "client" side of the connection. We just call them `Peers`.
 * **Bi-directional streams**: Each peer can _ask_ for a stream to the other peer. Useful for _NAT Traversal_
   or _server-side push_ scenarii.
 * **TCP Fiendly**: It has been designed to be friendly with TCP/IP network streams over Ethernet connections.
   The packet sizes has been designed to fit the most common network topologies on the Internet.
 * **Push-Back Protection**: A common problem with multiplexing is data can accumulated for a stream on the
   other peer, waiting to be processed. A stream-level _ACK_ (`DACK` packets) mecanism will prevent this problem.
 * **Pipelining**: Many packets can be sent before waiting for _ACT_ from the other peer. This will maximize
   the bandwidth usage and minimize DATA -> DATA-ACK -> DATA -> DATA-ACK latencies.
 * **Keep-alive mecanism**: To ensure the connection is kept alive, a keep-alive mecanism is built-in in
   the protocol. Useful for _NAT Traversal_ to force the firewall state to stay alive.
 * **Name-based streamd**: When opening a stream to the other peer, a full name is used and the other peer
   can choose to accept or reject the request.

 Features of the .NET implementation:
 * **Async & Multi-threaded**: Based on the .NET TPL Library and the _Async Framework_, the operations of the YMESP
   .NET implementation is both _parallel_ (multi-threaded) and _asynchroneous_ (non-blocking operations).
   The hosting application can optionnaly supply its own scheduler for multithreading operations.
 * **Fully compatible with existing code**: The streams are `System.Net.Stream` implementation. Any piece of
   code designed to used streams in .NET should works correctly with YMESP's virtual streams.
 * **Few Dependencies**: The .NET implementation of YMESP is using the native framework directly with very
   few dependencies, to ensure maximal interoperability. The only hard dependency is the
   _Microsoft Immutable Collections_ project used for threading safety.

## Sample Code using the YMESP .NET Implementation
Let's start first with a very simple code in .NET:
``` csharp
  public static async void Main(string[] args)
  {
    // The other peer just need to connect to it using a TcpCLient
    // It's just a sample: any stream will work!
    // Feel free to encrypt it or use another transport like a serial port!
    var server = new TcpListener(1234); // Wait for connection on TCP 1234
    var tcp = await server.AcceptTcpClientAsync();
    var lowLevelStream = tcp.GetStream();

    // Create the multiplexer
    IMultiplexer multiplexer = new Multiplexer(lowLevelStream);
    multiplexer.RequestedStream += OnRequestedStream; // register for other peers asking for streams
    multiplexer.Start(); // ensure you are handling the .RequestStream before starting it!

    // As an example, request few streams to other peer
    Stream stream1 = await multiplexer.RequestStream("A");
    if(stream1 == null)
    {
      // stream "A" rejected
      return;
    }
    Stream stream2 = await multiplexer.RequestStream("B");
    if(stream2 == null)
    {
      // stream "B" rejected
    }

    // Here do what you want with your newly created streams!

    // When finished
    multiplexer.Dispose(); // will close everything (even the lowLevelStream)
  }

  private static void OnRequestedStream(object sender, StreamRequestEventArgs args)
  {
    switch(args.Name)
    {
      case "C":
        ProcessStreamC(args.GetStream());
        return;
      case "D":
        ProcessStreamD(args.GetStream());
        return;
    }
    // after that the stream request is rejected because no handler called the args.GetStream()
  }
```

## Vocabulary
 * **Low-Level Stream** : This is the stream in which "everything" is passing.
 * **Peer** / **Other Peer**: Represents one end of the _low-level stream_.
 * **Stream** (or **Multiplexed Stream**): This is a multiplexed stream, tunnelled in the _low-level stream_.
 * **Stream Id**: The is the 32 bits identifier to refer to a _stream_.
 * **Stream Name**: This is the name of the stream, used by the requester to "ask" to for a
   stream on the other peer.

## Not covered by this protocol
This protocol is a building block for building complex applications.

Here some parts not covered by this protocol:
  * **Establishment of the _low-level stream_**: It could be any kind of reliable connection stream.
    Usually a TCP/IP stream, it could be anything else. Should work with a serial port, by example.
    For testing purposes, we use a YMSP stream into another one.
  * **Security/Encryption**: This protocol is plain-text. It means if you need something secure, you should
    wrap it in secure channel. `System.Net.Security.SslStream` in .NET is a good start if you need this.
  * **DOS protection**: There is no mecanism to prevent DOS-type attack on this protocol. Should be managed on the
    _low-level stream_.

## Important about sub-Streams
 * Sub-streams are of type `MultiplexerStream`, a nested type to `Multiplexer`.
 * Sub-streams are buffered: **it means data they won't be sent until the buffer is full or until you
   explicitely flush it**.

# Low-Level Stream Protocol - UNDER THE HOOD
 1. The first bytes (`ackBytes`) are sent by both peers.
    * If the bytes are not recognized by both peers, the stream is closed.
    * Default bytes is the 12 bytes `Yllibed.MUX.` (transmitted one byte per character, i.e. UTF-8 w/o BOM)
      but can be overriden in constructor.
 2. The following byte is the protocol version
    * Current version is `1` (`0x01`, not the character).
    * Any unsupported values from other peer should close the stream as not accepted.
 3. The following byte is a _bit-flags_ options.

    | bit | usage                                   |
    | ---:| --------------------------------------- |
    |  0  | If the CRC-32 is available on the peer. |
    |  1  | Undefined - should always be "false"    |
    |  2  | Undefined - should always be "false"    |
    |  3  | Undefined - should always be "false"    |
    |  4  | Undefined - should always be "false"    |
    |  5  | Undefined - should always be "false"    |
    |  6  | Undefined - should always be "false"    |
    |  7  | Undefined - should always be "false"    |
    > Bit #0 is the LSB.

 4. The followings format will be repeated until the end of the stream:
 
    | offset | length   | bits | format     | name        | usage                                            |
    |:------:|:--------:|:----:| ---------- | ----------- |:------------------------------------------------ |
    |      0 |        2 |   16 | `ushort`   | `streamId`  | The concerned stream number. `0` is reserved.    |
    |      2 |        1 |    8 | `byte`     | `type`      | The packet type (see section below)              |
    |      3 |        1 |    8 | `byte`     | _undefined_ | Reserved for future use - should always be 0x00. |
    |      4 |        2 |   16 | `ushort`   | `length`    | Length of payload (can be zero)                  |
    |      7 | `length` |  ... | _raw data_ | payload     | This is the payload of the packet                |

## Packet Types

| code   | name   | meaning                  | payload                                                 |
| ------:| ------ | ------------------------ | ------------------------------------------------------- |
| `0x00` | `NOP`  | No OPeration             | _empty_                                                 |
| `0x01` | `REQ`  | Stream request           | `windowSize` (ushort) + `streamName` (in UTF-8 w/o BOM) |
| `0x02` | `ACK`  | Stream request accepted  | `windowSize` (ushort)                                   |
| `0x03` | `NAK`  | Stream request rejected  | _empty_                                                 |
| `0x04` | `COL`  | Stream request collision | `suggestedId` (ushort)                                  |
| `0x10` | `DATA` | Payload data             | raw data (max 1402 bytes) + [CRC-32 or `0x00000000`]    |
| `0x11` | `DACK` | Payload data acknowledge | _empty_                                                 |
| `0xFE` | `ERR`  | Stream error             | `errorCode` (ushort)                                    |
| `0xFF` | `FIN`  | Stream closed            | `terminationType` (byte)                                |

## Requesting a stream
 1. One peer pick a tentative `streamId` (could be incremental or random - not important).
    **Stream id 0 (zero) is reserved**.
 2. A `REQ` packet is sent to the other end using the tentative streamId.
    * This packet starts with the other end's window size (unsigned 16 bits)
    * The remaining of the payload is a UTF-8 name of the stream.
 3. Wait for any packet for this `streamId`:
    * if `ACK`, we can consider the stream as opened and proceed with packets using provided window size.
    * if `NAK`, means the other peer rejected creating this stream
    * if `COL`, means a collision and must restart at step 1.
 4. Proceeed to payload data for this stream.

## Data Exchange & Flow Control
1. Any peer can send any `DATA` packet to the other peer and wait for a `DACK`. If the CRC-32 fail,
   an `ERR_CRC_FAIL` error should be sent back, followed by a `FIN` packet. The validation of the CRC-32
   is optional but recommended. A CRC-32 with value 0x0000 means the sending peer didn't calculated it.

2. _Pipelining Mode_: A peer can sent many `DATA` packets before waiting for corresponding `DACK` packets.
   The only restriction is the other peer's window size who should never be overreached.  If this occurres,
   an `ERR_BUFFER_OVERFLOW` could be generated by other peer.

3. While waiting for `DACK` packets, the corresponding _write operation_ will be blocked (for
   the concerned stream only). This mecanism will prevent the common "push-back" problem well known
   in multiplexing protocols.

4. Zero-length payload `DATA` packets are useless, but they are valid in this protocol and got no special meaning.

> **VERY IMPORTANT**: each `DATA` received must result in a `DACK` response.

## Terminating a Stream
* As soon a peer send a `FIN` packet, the concerned stream is closed.
* Buffers will be deallocated.

## Stream Ids
1. The id of the stream can be any non-zero values.
2. The stream 0 is reserved for future signaling purposes.
3. If a peer received an id already allocated in its table, it should simply return a
   `COL` packet with a suggested Id the other peer can use (could be ignored).
4. When a stream is closed, its id can be reused. As soon the other peer accepts it, it's valid.

Streams ids are not exposed to the application, because no application code should rely on it.

## Stream Names
The name of the stream can be anything. Any UTF-8 compliant value is accepted.
The meaning of the name is application-specific.

The max length of the name is limited to the packet size. **The maximum length is 1404 bytes of
UTF-8 characters**. As a precaution, the name length is limited 1024 characters (latin-equivalent).
If you need to deal with non-latin character sets, do your maths :-).

Zero length names are valid too on this protocol. It's up to the other peer to decide if
the stream request is valid or not.

> The way the Stream names has been designed is to use them as service names. Use them as
> _port number_ in _TCP/IP_ or `WebSocket-Protocol` in the _WebSocket_ stream negociation.

## Keep-Alive Mecanism
A `NOP` packet could be sent on any stream (usually the pseudo-stream 0) as a keep-alive mecanism.

* Such packet should be sent when there's nothing transfered for more than 15 seconds.
* A peer can close the low-level connection when nothing is received for more than 30 seconds.
* The keep-alive mecanism is for the low-level stream only. There is no timeout mecanism for streams.

## Error Codes

| code   | name                      | meaning                                                          |
| ------:| ------------------------- | ---------------------------------------------------------------- |
| 0x0F01 | `ERR_UNKNOWN_PACKET_TYPE` | An unknown packet type has been sent. (a `FIN` should follow)    |
| 0x0F02 | `ERR_CRC_FAIL`            | A `DATA` packet failed CRC-32 check.                             |
| 0x0F03 | `ERR_BUFFER_OVERFLOW`     | An unknown packet type has been sent. (a `FIN` should follow)    |
| 0x0F04 | `ERR_TIME_OUT`            | Nothing received for too.  (a `FIN` should follow)               |
| 0x0F05 | `ERR_PACKET_TOO_LONG`     | A packet is received exceeding the limit packet size.            |
| 0x0F06 | `ERR_PACKET_TOO_SHORT`    | A packet length is too short to hold required information.       |

## Termination Types

| code | name       | meaning                                                             | source         |
| ----:| ---------- | ------------------------------------------------------------------- | -------------- |
| 0x00 | `NORMAL`   | The stream terminated normally.                                     | Peer App       |
| 0x01 | `PROTOCOL` | The stream terminated by a protocol (YMESP) error.                  | YMESP Protocol |
| 0x02 | `ERROR`    | The stream terminated by an error (emitted by the application peer) | Peer App       |

## FAQ
### Why the `DATA` payload is limited to 1402 bytes?

   Calculations:
   * Most modern networks (WIFI, LAN and WANs) are using the _Ethernet_ for the physical connections.
   * The `Ethernet II` (the most common _Ethernet_ frame type) common frame size is **1518 bytes**.
   * The `Ethernet II` header size is **14 bytes**, and the _CRC-32_ is **4 bytes**,
     leaving **1500 bytes** for the `IP` packet.
   * Some Internet providers will used encapsulating protocols like `PPPoE`. This protocol takes a
     **8 bytes** overhead, leaving **1492 bytes** for the `IP` packet.
   * The `IPv4` packet header usually take **20 bytes**, while the `IPv6` header is typically **40 bytes**,
     leaving **1432 bytes** for the `TCP` transport protocol. We'lll use `IPv6` header size
     for the calculation.
   * The `TCP` header usually take **20 bytes** (without options), leaving **1412 bytes** for the YMESP packet.
   * The `YMESP` header is **6 bytes**, and the `DATA` payload requires a **4 bytes** CRC value,
     leaving **1402 bytes** for the payload.
   * Remember this is only a tweak for performance purposes. Other network configurations will work well too.

| Layer / Protocol    | Overhead       | Ov. Size |   Remaining    |
| ------------------- | -------------- | --------:|:--------------:|
| Ethernet (physical) | -              |        - |   1518 bytes   |
| Ethernet II Frame   | MAC Addressing | 18 bytes |   1500 bytes   |
| ISP Encapsulation   | PPPoE Header   |  8 bytes |   1492 bytes   |
| IPv6                | IPv6 Header    | 40 bytes |   1432 bytes   |
| TCP                 | TCP Header     | 20 bytes |   1412 bytes   |
| YMESP Packet        | YMESP Header   |  6 bytes |   1406 bytes   |
| YMESP _DATA_        | _DATA_ CRC-32  |  4 bytes | **1402 bytes** |

> **Stream Name**: This calculation also mean the
>   **max length for a `streamName` is 1404 bytes**,
>   encoded as UTF-8. Depending on the choosen characters set, the
>   **string length should be limited to 1024 latin characters**.

### What are the CRC-32 Settings?
This protocol is the common _CRC-32 Reversed Polynomial Representation_ of `0xEDB88320` (the same settings as used by ZMODEM, GZip, MPEG-2, PNG and Ethernet IEEE 802.3 Frames).

> The CRC-32 is **used only when both peers activated it** in their stream establishment.

### What is the endianness of the values?
All values are transfered using the _Little-Endian_ format, as in Intel processors. This mean the
bytes are transferred in this order in the stream:

| value type       | byte transmit order | sample value | sample transmit order  |
| ----------------:| -------------------:| ------------:| ---------------------- |
| byte             | b0                  | 0x12         | 0x12                   |
| ushort (16 bits) | b0, b1              | 0x1234       | 0x34, 0x12             |
| uint (32 bits)   | b0, b1, b2, b3      | 0x12345678   | 0x78, 0x56, 0x34, 0x12 |
> byte 0 is the LSB

> Lower-level protocols (TCP / IP / Ethernet) are using _Big-Endian_.
> We're using little endian here because it's straight-forward with .NET
> and Intel processors. Implementation like _Mono_ (used by _Xamarin_) will
> also use little endian on ARM processors.

## Implementations
This project contains an implementation using C#/.NET.

> Other implementations are welcome.

## Potential Future Improvements / ambitions
* Performance enhancements: Less CPU, less memory-copying, more throughtput.
* Support for dynamic window instead of relying on static window size.
* Implement a kind of priority on substreams.
* Ability to send unsollicited OOB (Out-Of-Band) packets to other peer on a stream.
* Ability to "ask" to other peer a list of available streams to connect to.
* Variable packet size based on a packet size discovery mecanism.
* Support for other languages / platforms.
* Add compression capabilities
* Buy the Apple's HQ "spaceship" to host the growing working team.

## Other Similar Projects / Technologies / Articles on Stream Multiplexing
 * Yamux (Yet another Multiplexer): <https://github.com/hashicorp/yamux>
   Very similar to YMESP, implementation in _Golang_.
 * Socket_hack: <https://gist.github.com/natevw/f7934b0f0ef49d8254b6>
 * RFC1078: <https://tools.ietf.org/html/rfc1078> "TCP Port Service Multiplexer (TCPMUX)"
   (defined in 1988 - this is more a protocol switcher than a multiplexer...)
 * RFC1692: <https://tools.ietf.org/html/rfc1692> "Transport Multiplexing Protocol (TMux)"
   (defined in 1994 - obsoleted, never implemented) TL;DR 
 * RFC4960: <https://tools.ietf.org/html/rfc4960> "Stream Control Transmission Protocol (SCTP)"
   SCTP is designed to transport Public Switched Telephone Network (PSTN) signaling messages
   over IP networks, but is capable of broader applications.
 * <https://devcentral.f5.com/articles/3-really-good-reasons-you-should-use-tcp-multiplexing>
 * HTTP/2 (SPDY) uses a similar multiplexing mecanism.


 
