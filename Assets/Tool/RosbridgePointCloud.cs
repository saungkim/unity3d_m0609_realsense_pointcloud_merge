// // using System;
// // using System.Collections.Generic;
// // using System.IO;
// // using System.Net.WebSockets;
// // using System.Threading;
// // using System.Threading.Tasks;
// // using UnityEngine;

// // public class FoxgloveWsClient : MonoBehaviour
// // {
// //     public string Url = "ws://127.0.0.1:8765"; // foxglove_bridge 기본 포트
// //     public string TargetTopic = "/camera/camera/depth/color/points";

// //     ClientWebSocket ws;
// //     CancellationTokenSource cts;

// //     // --- DTOs ---
// //     [Serializable] class Channel { public uint id; public string topic; public string encoding; public string schemaName; }
// //     [Serializable] class Advertise { public string op; public Channel[] channels; }
// //     [Serializable] class SubscribeReq { public string op = "subscribe"; public Sub[] subscriptions; }
// //     [Serializable] class Sub { public uint id; public uint channelId; } // ✅ id 추가
// //     // -------------

// //     uint? channelId;
// //     uint subReqId = 1; // ✅ 구독 요청마다 고유 id 부여
// //     ulong recvCount;

// //     async void Start()
// //     {
// //         Application.runInBackground = true;
// //         cts = new CancellationTokenSource();
// //         ws = new ClientWebSocket();

// //         // foxglove 서브프로토콜 필수
// //         ws.Options.AddSubProtocol("foxglove.websocket.v1");
// //         ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

// //         await ws.ConnectAsync(new Uri(Url), cts.Token);
// //         Debug.Log("[FG] Connected " + Url);

// //         _ = ReceiveLoop();
// //     }

// //     async Task ReceiveLoop()
// //     {
// //         var chunk = new byte[1 << 20];
// //         while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
// //         {
// //             using var ms = new MemoryStream();
// //             WebSocketReceiveResult res;
// //             do
// //             {
// //                 res = await ws.ReceiveAsync(new ArraySegment<byte>(chunk), cts.Token);
// //                 if (res.MessageType == WebSocketMessageType.Close)
// //                 {
// //                     await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
// //                     Debug.Log("[FG] closed by server");
// //                     return;
// //                 }
// //                 ms.Write(chunk, 0, res.Count);
// //             } while (!res.EndOfMessage);

// //             var buf = ms.ToArray();

// //             if (res.MessageType == WebSocketMessageType.Text)
// //             {
// //                 string json = System.Text.Encoding.UTF8.GetString(buf);
// //                 if (json.Contains("\"op\":\"advertise\""))
// //                 {
// //                     var adv = JsonUtility.FromJson<Advertise>(json);
// //                     if (adv?.channels != null)
// //                     {
// //                         foreach (var ch in adv.channels)
// //                         {
// //                             if (ch.topic == TargetTopic)
// //                             {
// //                                 channelId = ch.id;
// //                                 await SendSubscribe(ch.id);
// //                                 Debug.Log($"[FG] Subscribed channelId={ch.id}, encoding={ch.encoding}");
// //                             }
// //                         }
// //                     }
// //                 }
// //             }
// //             else
// //             {
// //                 // Binary MESSAGE_DATA
// //                 if (buf.Length < 1 + 4 + 8) continue;
// //                 byte opcode = buf[0];
// //                 if (opcode != 0x01) continue; // 0x01: MESSAGE_DATA

// //                 uint cid = BitConverter.ToUInt32(buf, 1);
// //                 ulong ts  = BitConverter.ToUInt64(buf, 5);
// //                 int payloadOffset = 1 + 4 + 8;
// //                 int payloadSize = buf.Length - payloadOffset;

// //                 if (channelId.HasValue && cid == channelId.Value)
// //                 {
// //                     recvCount++;
// //                     if (recvCount % 30 == 0)
// //                         Debug.Log($"[FG] recv #{recvCount} payload={payloadSize} bytes ts={ts}");
// //                     // TODO: payload(CDR) 파싱 추가
// //                 }
// //             }
// //         }
// //     }

// //     async Task SendSubscribe(uint cid)
// //     {
// //         // ✅ 프로토콜 요구사항: id + channelId 둘 다 포함
// //         var req = new SubscribeReq {
// //             subscriptions = new[] { new Sub { id = subReqId++, channelId = cid } }
// //         };
// //         string json = JsonUtility.ToJson(req);
// //         var bytes = System.Text.Encoding.UTF8.GetBytes(json);
// //         await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
// //     }

// //     async void OnDestroy()
// //     {
// //         try
// //         {
// //             cts?.Cancel();
// //             if (ws != null && ws.State == WebSocketState.Open)
// //                 await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
// //         }
// //         catch { }
// //         finally { ws?.Dispose(); cts?.Dispose(); }
// //     }
// // }

// using System;
// using System.Collections.Generic;
// using System.IO;
// using System.Net.WebSockets;
// using System.Threading;
// using System.Threading.Tasks;
// using UnityEngine;

// public class FoxglovePointCloudRenderer : MonoBehaviour
// {
//     [Header("Foxglove")]
//     public string Url = "ws://127.0.0.1:8765";
//     public string TargetTopic = "/camera/camera/depth/color/points";

//     [Header("Render")]
//     public int   MaxPoints = 200_000;
//     public bool  swapZYAxes = true;  // ROS(Z-up) → Unity(Y-up) 보정
//     public bool  logEvery30 = true;

//     // --- Unity render state ---
//     Mesh mesh;
//     Vector3[] vertices;
//     Color[] colors;
//     int[] indices;

//     // --- WS state ---
//     ClientWebSocket ws;
//     CancellationTokenSource cts;
//     uint? channelId;
//     uint subReqId = 1;
//     ulong recvCount;

//     // ---------- Foxglove JSON DTOs ----------
//     [Serializable] class Channel { public uint id; public string topic; public string encoding; public string schemaName; }
//     [Serializable] class Advertise { public string op; public Channel[] channels; }
//     [Serializable] class SubscribeReq { public string op = "subscribe"; public Sub[] subscriptions; }
//     [Serializable] class Sub { public uint id; public uint channelId; }
//     // ----------------------------------------

//     // ---------- Minimal CDR reader ----------
//     class CdrReader
//     {
//         readonly byte[] b; int p; bool little;

//         public CdrReader(byte[] data, int offset)
//         {
//             b = data; p = offset;
//             // Encapsulation: 4 bytes (CDR/XCDR1 header). bit0 = endianness (1 = LE)
//             uint encap = BitConverter.ToUInt32(b, p);
//             p += 4;
//             little = (encap & 0x00000001u) != 0;
//         }
//         void Align(int n) { int m = p % n; if (m != 0) p += (n - m); }
//         public int Pos => p;

//         public bool Little => little;

//         public uint ReadU32() { Align(4); uint v = little ? BitConverter.ToUInt32(b, p) : Swap32(BitConverter.ToUInt32(b, p)); p += 4; return v; }
//         public int  ReadI32() { Align(4); int  v = little ? BitConverter.ToInt32 (b, p) : (int)Swap32((uint)BitConverter.ToInt32(b, p)); p += 4; return v; }
//         public bool ReadBool(){ Align(1); bool v = b[p] != 0; p += 1; return v; }
//         public byte ReadU8()  { Align(1); byte v = b[p]; p += 1; return v; }
//         public string ReadString()
//         {
//             uint len = ReadU32();               // includes null terminator (Fast-CDR)
//             if (len == 0) return string.Empty;
//             int n = (int)len;
//             string s = System.Text.Encoding.UTF8.GetString(b, p, n - 1); // drop trailing '\0'
//             p += n;
//             Align(4);
//             return s;
//         }
//         public byte[] ReadBytesU8()
//         {
//             uint len = ReadU32();
//             byte[] dst = new byte[len];
//             Buffer.BlockCopy(b, p, dst, 0, (int)len);
//             p += (int)len;
//             Align(4);
//             return dst;
//         }

//         static uint Swap32(uint x) =>
//             ((x & 0x000000FFu) << 24) | ((x & 0x0000FF00u) << 8) | ((x & 0x00FF0000u) >> 8) | ((x & 0xFF000000u) >> 24);
//     }
//     // ----------------------------------------

//     // ---------- PointField DTO ----------
//     struct PF { public string name; public uint offset; public byte datatype; public uint count; }
//     // ------------------------------------

//     struct Frame { public int n; public Vector3[] v; public Color[] c; }
//     Frame? latest;
//     readonly object latestLock = new object();

//     async void Start()
//     {
//         Application.runInBackground = true;

//         // mesh init
//         mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
//         var mf = GetComponent<MeshFilter>(); if (mf) mf.mesh = mesh;
//         var mr = GetComponent<MeshRenderer>();
//         if (mr && mr.sharedMaterial == null)
//             mr.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));

//         vertices = new Vector3[MaxPoints];
//         colors   = new Color[MaxPoints];
//         indices  = new int[MaxPoints];
//         for (int i = 0; i < MaxPoints; i++) indices[i] = i;

//         // ws connect
//         cts = new CancellationTokenSource();
//         ws  = new ClientWebSocket();
//         ws.Options.AddSubProtocol("foxglove.websocket.v1");
//         ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

//         await ws.ConnectAsync(new Uri(Url), cts.Token);
//         Debug.Log("[FG] Connected: " + Url);

//         _ = ReceiveLoop();
//     }

//     async Task ReceiveLoop()
//     {
//         var chunk = new byte[1 << 20]; // 1MB
//         while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
//         {
//             using var ms = new MemoryStream();
//             WebSocketReceiveResult res;
//             do
//             {
//                 res = await ws.ReceiveAsync(new ArraySegment<byte>(chunk), cts.Token);
//                 if (res.MessageType == WebSocketMessageType.Close)
//                 {
//                     await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
//                     Debug.Log("[FG] closed by server");
//                     return;
//                 }
//                 ms.Write(chunk, 0, res.Count);
//             } while (!res.EndOfMessage);

//             var buf = ms.ToArray();

//             if (res.MessageType == WebSocketMessageType.Text)
//             {
//                 string json = System.Text.Encoding.UTF8.GetString(buf);
//                 if (json.Contains("\"op\":\"advertise\""))
//                 {
//                     var adv = JsonUtility.FromJson<Advertise>(json);
//                     if (adv?.channels != null)
//                     {
//                         foreach (var ch in adv.channels)
//                         {
//                             if (ch.topic == TargetTopic)
//                             {
//                                 channelId = ch.id;
//                                 await SendSubscribe(ch.id);
//                                 Debug.Log($"[FG] Subscribed ch={ch.id} enc={ch.encoding}");
//                             }
//                         }
//                     }
//                 }
//             }
//             else
//             {
//                 // Foxglove binary MESSAGE_DATA
//                 if (buf.Length < 1 + 4 + 8) continue;
//                 byte opcode = buf[0];
//                 if (opcode != 0x01) continue; // 0x01 = MESSAGE_DATA

//                 uint cid = BitConverter.ToUInt32(buf, 1);
//                 if (!channelId.HasValue || cid != channelId.Value) continue;

//                 // ulong ts = BitConverter.ToUInt64(buf, 5); // 필요시 사용
//                 int payloadOffset = 1 + 4 + 8;
//                 int payloadSize   = buf.Length - payloadOffset;

//                 ParsePointCloud2(buf, payloadOffset, payloadSize);

//                 recvCount++;
//                 if (logEvery30 && (recvCount % 30 == 0))
//                     Debug.Log($"[FG] recv #{recvCount} payload={payloadSize} bytes");
//             }
//         }
//     }

//     async Task SendSubscribe(uint cid)
//     {
//         var req = new SubscribeReq { subscriptions = new[] { new Sub { id = subReqId++, channelId = cid } } };
//         string json = JsonUtility.ToJson(req);
//         var bytes = System.Text.Encoding.UTF8.GetBytes(json);
//         await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
//     }

//     // ---- CDR → PointCloud2 → Frame ----
//     void ParsePointCloud2(byte[] data, int ofs, int len)
//     {
//         var r = new CdrReader(data, ofs);

//         // std_msgs/Header header
//         // builtin_interfaces/Time stamp
//         r.ReadI32(); // sec
//         r.ReadU32(); // nanosec
//         r.ReadString(); // frame_id

//         // height, width
//         uint height = r.ReadU32();
//         uint width  = r.ReadU32();

//         // fields[]
//         uint nFields = r.ReadU32();
//         var fields = new PF[nFields];
//         for (int i = 0; i < nFields; i++)
//         {
//             PF pf = new PF();
//             pf.name     = r.ReadString();
//             pf.offset   = r.ReadU32();
//             pf.datatype = r.ReadU8();
//             pf.count    = r.ReadU32();
//             fields[i] = pf;
//         }

//         bool msg_is_bigendian = r.ReadBool();
//         uint point_step = r.ReadU32();
//         uint row_step   = r.ReadU32();
//         byte[] raw = r.ReadBytesU8();   // data
//         bool  is_dense = r.ReadBool();  // consume final bool

//         // find offsets
//         int offX=-1, offY=-1, offZ=-1, offRGB=-1;
//         foreach (var f in fields)
//         {
//             if      (f.name == "x")    offX = (int)f.offset;
//             else if (f.name == "y")    offY = (int)f.offset;
//             else if (f.name == "z")    offZ = (int)f.offset;
//             else if (f.name == "rgb" || f.name == "rgba") offRGB = (int)f.offset;
//         }
//         if (offX < 0 || offY < 0 || offZ < 0) return;

//         int total = (int)Math.Min((long)width * height, (long)raw.Length / point_step);
//         int n = Mathf.Min(total, MaxPoints);

//         var v = new Vector3[n];
//         var c = new Color[n];

//         // helper for LE/BE float/uint32 in point data
//         float ReadF(byte[] src, int o)
//         {
//             if (!msg_is_bigendian) return BitConverter.ToSingle(src, o);
//             // swap 4 bytes
//             byte b0=src[o], b1=src[o+1], b2=src[o+2], b3=src[o+3];
//             uint u = (uint)(b0<<24 | b1<<16 | b2<<8 | b3);
//             unsafe { float* fp = (float*)&u; return *fp; }
//         }
//         uint ReadU32beAware(byte[] src, int o)
//         {
//             if (!msg_is_bigendian) return BitConverter.ToUInt32(src, o);
//             byte b0=src[o], b1=src[o+1], b2=src[o+2], b3=src[o+3];
//             return (uint)(b0<<24 | b1<<16 | b2<<8 | b3);
//         }

//         for (int i = 0; i < n; i++)
//         {
//             int baseOfs = i * (int)point_step;
//             float x = ReadF(raw, baseOfs + offX);
//             float y = ReadF(raw, baseOfs + offY);
//             float z = ReadF(raw, baseOfs + offZ);

//             v[i] = swapZYAxes ? new Vector3(x, z, -y) : new Vector3(x, y, z);

//             if (offRGB >= 0)
//             {
//                 uint rgb = ReadU32beAware(raw, baseOfs + offRGB);
//                 byte R = (byte)((rgb >> 16) & 0xFF);
//                 byte G = (byte)((rgb >> 8 ) & 0xFF);
//                 byte B = (byte)((rgb      ) & 0xFF);
//                 c[i] = new Color(R/255f, G/255f, B/255f, 1f);
//             }
//             else c[i] = Color.white;
//         }

//         lock (latestLock)
//             latest = new Frame { n = n, v = v, c = c };
//     }

//     void Update()
//     {
//         Frame? f;
//         lock (latestLock) { f = latest; latest = null; }
//         if (!f.HasValue) return;

//         int n = f.Value.n;
//         Array.Copy(f.Value.v, 0, vertices, 0, n);
//         Array.Copy(f.Value.c, 0, colors,   0, n);

//         if (indices.Length < n)
//         {
//             indices = new int[n];
//             for (int i = 0; i < n; i++) indices[i] = i;
//         }

//         mesh.Clear();
//         mesh.SetVertices(vertices, 0, n);
//         mesh.SetColors(colors, 0, n);
//         mesh.SetIndices(indices, 0, n, MeshTopology.Points, 0, false);
//     }

//     async void OnDestroy()
//     {
//         try
//         {
//             cts?.Cancel();
//             if (ws != null && ws.State == WebSocketState.Open)
//                 await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
//         }
//         catch { }
//         finally { ws?.Dispose(); cts?.Dispose(); }
//     }
// }

// RosbridgePointCloud.cs — 파싱 루프가 배열을 매번 new 하지 않도록 수정 예시
// - 바이트 스트림 → 벡터/컬러 파싱 시, 외부에서 버퍼를 주입받아 거기에 write
// - 여기서는 예시 시그니처만. 실제 프로젝트 파서 구조에 맞게 적용.

using System;
using UnityEngine;

public static class RosbridgePointCloud
{
    // 예시: ROS msg의 fields를 해석해 xyz/rgb를 wr 버퍼에 채운다.
    public static int ParseIntoBuffers(byte[] msg, Vector3[] outVerts, Color32[] outCols)
    {
        // TODO: 실제 포맷에 맞춰 파싱.
        // 반환값 = 채운 포인트 개수
        int n = Math.Min(outVerts.Length, outCols.Length);

        // ...파싱 루프에서 outVerts[i] / outCols[i] 직접 채우기...
        // new List/Array 생성 없이 끝내는 것이 핵심.

        return n;
    }
}
