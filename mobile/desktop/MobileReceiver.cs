using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ContentMover {
    public sealed class MobileSettings { public bool Enabled,Remote; public string Key="",LocalAddress="",RemoteAddress=""; }
    public sealed class MobileImage { public string Data=""; }
    public sealed class MobileClip { public string Id="",Text="",Title="",Note="",Source=""; public List<MobileImage> Images=new List<MobileImage>(); }
    public sealed class Envelope { public string iv="",data="",mac=""; }
    public static class MobileCrypto {
        static byte[] Derive(byte[] key,string purpose){using(var hash=SHA256.Create()){var bytes=new byte[key.Length+purpose.Length];Buffer.BlockCopy(key,0,bytes,0,key.Length);Encoding.ASCII.GetBytes(purpose,0,purpose.Length,bytes,key.Length);return hash.ComputeHash(bytes);}}
        public static string Seal(byte[] key,string text){using(var aes=Aes.Create()){aes.Key=Derive(key,"enc");aes.GenerateIV();byte[] data;using(var enc=aes.CreateEncryptor()){var bytes=Encoding.UTF8.GetBytes(text);data=enc.TransformFinalBlock(bytes,0,bytes.Length);}var signed=new byte[16+data.Length];Buffer.BlockCopy(aes.IV,0,signed,0,16);Buffer.BlockCopy(data,0,signed,16,data.Length);using(var hmac=new HMACSHA256(Derive(key,"mac")))return new JavaScriptSerializer().Serialize(new Envelope{iv=Convert.ToBase64String(aes.IV),data=Convert.ToBase64String(data),mac=Convert.ToBase64String(hmac.ComputeHash(signed))});}}
        public static string Open(byte[] key,string text){var json=new JavaScriptSerializer{MaxJsonLength=30000000};var env=json.Deserialize<Envelope>(text);var iv=Convert.FromBase64String(env.iv);var data=Convert.FromBase64String(env.data);var mac=Convert.FromBase64String(env.mac);if(iv.Length!=16 || mac.Length!=32 || data.Length>22000000)throw new InvalidDataException("消息无效");var signed=new byte[16+data.Length];Buffer.BlockCopy(iv,0,signed,0,16);Buffer.BlockCopy(data,0,signed,16,data.Length);byte[] expected;using(var hmac=new HMACSHA256(Derive(key,"mac")))expected=hmac.ComputeHash(signed);int difference=0;for(int i=0;i<32;i++)difference|=mac[i]^expected[i];if(difference!=0)throw new InvalidDataException("配对验证失败");using(var aes=Aes.Create()){aes.Key=Derive(key,"enc");aes.IV=iv;using(var dec=aes.CreateDecryptor())return Encoding.UTF8.GetString(dec.TransformFinalBlock(data,0,data.Length));}}
        public static byte[] NewKey(){var key=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(key);return key;}
    }
    public sealed class MobileStore {
        readonly string outputDirectory,receiptDirectory;readonly object gate=new object();
        public MobileStore(string output,string state){outputDirectory=output;receiptDirectory=Path.Combine(state,"receipts");Directory.CreateDirectory(receiptDirectory);}
        public string Save(MobileClip clip){lock(gate){Guid id;if(clip==null || !Guid.TryParseExact(clip.Id,"N",out id))throw new InvalidDataException("编号无效");if(clip.Text==null || clip.Text.Length>200000 || (clip.Note??"").Length>200000 || (clip.Title??"").Length>1000 || (clip.Source??"").Length>4000 || clip.Images==null || clip.Images.Count>10)throw new InvalidDataException("内容过大");if(String.IsNullOrWhiteSpace(clip.Text) && clip.Images.Count==0)throw new InvalidDataException("没有内容");string receipt=Path.Combine(receiptDirectory,clip.Id+".json");DateTime day=DateTime.Today;string output=OutputFiles.DatedPath(outputDirectory,day);if(File.Exists(receipt)){var prior=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(receipt));output=prior["output"];day=DateTime.ParseExact(prior["day"],"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);}
            string marker="<!-- content-mover-mobile:"+clip.Id+" -->";if(File.Exists(output) && File.ReadAllText(output).Contains(marker))return output;
            var validated=new List<byte[]>();long total=0;foreach(var image in clip.Images){if(image==null || image.Data==null || image.Data.Length>14000000)throw new InvalidDataException("图片过大");byte[] raw=Convert.FromBase64String(image.Data);total+=raw.Length;if(total>16000000)throw new InvalidDataException("图片总大小超过 16 MB");using(var stream=new MemoryStream(raw))using(var bitmap=Image.FromStream(stream,true,true)){if((long)bitmap.Width*bitmap.Height>24000000)throw new InvalidDataException("图片像素过大");using(var normalized=new Bitmap(bitmap))using(var png=new MemoryStream()){normalized.Save(png,System.Drawing.Imaging.ImageFormat.Png);validated.Add(png.ToArray());}}}
            File.WriteAllText(receipt,new JavaScriptSerializer().Serialize(new{output=output,day=day.ToString("yyyy-MM-dd")}),new UTF8Encoding(false));
            string folder="assets"+day.ToString("yyyy-M-d");var note=new StringBuilder(clip.Note??"");for(int i=0;i<validated.Count;i++){string filename="mobile-"+clip.Id+"-"+(i+1)+".png";string directory=Path.Combine(outputDirectory,folder);Directory.CreateDirectory(directory);File.WriteAllBytes(Path.Combine(directory,filename),validated[i]);note.Append("\r\n\r\n![手机截图](").Append(folder).Append('/').Append(filename).Append(")\r\n");}
            var content=new Clip{Title=String.IsNullOrWhiteSpace(clip.Title)?"手机摘录":clip.Title,Text=clip.Text+"\r\n\r\n"+marker,App="内容迁移 · Android",CapturedAt=DateTime.Now};content.Detail="手机传入；按电脑接收日期归档";Storage.Append(output,content,note.ToString(),clip.Source);return output;
        }}
    }
    public sealed class MobileServer:IDisposable {
        TcpListener listener;readonly byte[] key;readonly MobileStore store;readonly bool allowRemote;readonly SemaphoreSlim clients=new SemaphoreSlim(4);public event Action<string> Saved;
        public MobileServer(byte[] sharedKey,MobileStore storage,bool remote=false){key=sharedKey;store=storage;allowRemote=remote;}
        public static bool Private(IPAddress address){byte[] a=address.GetAddressBytes();return a.Length==4 && (a[0]==127 || a[0]==10 || (a[0]==172 && a[1]>=16 && a[1]<=31) || (a[0]==192 && a[1]==168) || (a[0]==169 && a[1]==254));}
        public static bool Tailscale(IPAddress address){byte[] a=address.GetAddressBytes();return a.Length==4 && a[0]==100 && a[1]>=64 && a[1]<=127 && !address.Equals(IPAddress.Parse("100.100.100.100"));}
        public static bool Allowed(IPAddress address,bool remote){return Private(address) || (remote && Tailscale(address));}
        public int Start(int port){return Start(port,IPAddress.Any);}
        public int Start(int port,IPAddress bindAddress){if(allowRemote && !Tailscale(bindAddress))throw new InvalidOperationException("远程接收必须绑定 Tailscale 地址");listener=new TcpListener(bindAddress,port);listener.Start();Task.Run((Action)Accept);return ((IPEndPoint)listener.LocalEndpoint).Port;}
        async void Accept(){var active=listener;while(listener==active){TcpClient client;try{client=await active.AcceptTcpClientAsync();}catch{return;}if(!Allowed(((IPEndPoint)client.Client.RemoteEndPoint).Address,allowRemote) || !clients.Wait(0)){client.Close();continue;}Task.Run(delegate{try{Handle(client);}finally{client.Close();clients.Release();}});}}
        void Handle(TcpClient client){try{client.ReceiveTimeout=10000;client.SendTimeout=10000;using(var stream=client.GetStream()){var header=new MemoryStream();int b;while(header.Length<8192 && (b=stream.ReadByte())>=0){header.WriteByte((byte)b);if(header.Length>=4){var h=header.GetBuffer();long n=header.Length;if(h[n-4]==13 && h[n-3]==10 && h[n-2]==13 && h[n-1]==10)break;}}string headers=Encoding.ASCII.GetString(header.ToArray());string[] lines=headers.Split(new[]{"\r\n"},StringSplitOptions.None);int length=0;if(lines[0]!="POST /v1/clips HTTP/1.1")throw new InvalidDataException();foreach(string line in lines){if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=Int32.Parse(line.Substring(15).Trim());if(line.StartsWith("Transfer-Encoding:",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException();}if(length<=0 || length>30000000)throw new InvalidDataException();var bytes=new byte[length];int offset=0;while(offset<length){int read=stream.Read(bytes,offset,length-offset);if(read==0)throw new EndOfStreamException();offset+=read;}string plaintext=MobileCrypto.Open(key,Encoding.UTF8.GetString(bytes));var json=new JavaScriptSerializer{MaxJsonLength=22000000};var clip=json.Deserialize<MobileClip>(plaintext);string output=store.Save(clip);string body=MobileCrypto.Seal(key,json.Serialize(new{id=clip.Id,saved=true,file=Path.GetFileName(output)}));byte[] payload=Encoding.UTF8.GetBytes(body);byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+payload.Length+"\r\nConnection: close\r\n\r\n");stream.Write(response,0,response.Length);stream.Write(payload,0,payload.Length);if(Saved!=null)Saved(output);}}catch{try{var bytes=Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");client.GetStream().Write(bytes,0,bytes.Length);}catch{}}}
        public void Dispose(){var current=listener;listener=null;if(current!=null)current.Stop();}
    }
    static class MobileProgram {
        [STAThread] static void Main(string[] args){AppDomain.CurrentDomain.AssemblyResolve+=delegate(object sender,ResolveEventArgs e){if(new AssemblyName(e.Name).Name!="zxing")return null;using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("zxing.dll")){var bytes=new byte[source.Length];source.Read(bytes,0,bytes.Length);return Assembly.Load(bytes);}};if(args.Length>1 && args[0]=="--self-test"){MobileTests.Run(args[1]);return;}if(args.Length>1 && args[0]=="--verify-interop"){MobileTests.VerifyInterop(args[1]);return;}Application.EnableVisualStyles();Native.InitializeDpi();bool created;using(var mutex=new Mutex(true,"Local.ContentMover.MobileReceiver",out created)){if(!created){MessageBox.Show("手机接收已运行，请在托盘打开手机连接窗口。","内容迁移");return;}Application.Run(new PairingForm(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ContentMoverMobile")));}}
    }
}
