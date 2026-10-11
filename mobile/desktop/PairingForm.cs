using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ContentMover {
    public sealed class PairingForm:Form {
        readonly MobileSettings settings;
        readonly string stateDirectory;
        readonly NotifyIcon tray;
        MobileServer server;
        readonly CheckBox enabled=new CheckBox(),remote=new CheckBox();
        readonly ComboBox addresses=new ComboBox();
        readonly PictureBox qr=new PictureBox();
        readonly Label status=new Label();
        bool loading;

        public PairingForm(string state){
            stateDirectory=state;Directory.CreateDirectory(state);
            string file=Path.Combine(state,"settings.json");
            settings=File.Exists(file)?new JavaScriptSerializer().Deserialize<MobileSettings>(File.ReadAllText(file)):new MobileSettings();
            if(String.IsNullOrEmpty(settings.Key))settings.Key=Convert.ToBase64String(MobileCrypto.NewKey());
            Text="内容迁移 · 手机连接";Icon=AppIcon.Load();Font=new Font("Microsoft YaHei UI",10);
            ClientSize=new Size(490,660);StartPosition=FormStartPosition.CenterScreen;
            MaximizeBox=false;FormBorderStyle=FormBorderStyle.FixedDialog;
            enabled.Text="允许手机发送内容";enabled.SetBounds(24,20,440,30);enabled.Checked=settings.Enabled;Controls.Add(enabled);
            remote.Text="远程连接（需手机与电脑都连接 Tailscale）";remote.SetBounds(24,55,440,30);remote.Checked=settings.Remote;Controls.Add(remote);
            addresses.SetBounds(24,96,440,32);addresses.DropDownStyle=ComboBoxStyle.DropDownList;Controls.Add(addresses);
            qr.SetBounds(95,142,300,300);qr.SizeMode=PictureBoxSizeMode.Zoom;Controls.Add(qr);
            status.SetBounds(24,456,440,112);Controls.Add(status);
            var refresh=new Button{Text="刷新地址",Left=24,Top=576,Width=115,Height=32};
            refresh.Click+=delegate{ReloadAddresses();RefreshServer();};Controls.Add(refresh);
            var reset=new Button{Text="重新配对",Left=150,Top=576,Width=115,Height=32};
            reset.Click+=delegate{if(MessageBox.Show(this,"重新配对后，旧手机需要再次扫码。继续吗？","重新配对",MessageBoxButtons.OKCancel)==DialogResult.OK){settings.Key=Convert.ToBase64String(MobileCrypto.NewKey());RefreshServer();}};Controls.Add(reset);
            var close=new Button{Text="隐藏到托盘",Left=330,Top=576,Width=135,Height=32};close.Click+=delegate{Hide();};Controls.Add(close);
            var help=new Label{Text="远程发送按电脑接收日期归档，手机发送会使用移动流量。",Left=24,Top=620,Width=440,Height=25};Controls.Add(help);
            tray=new NotifyIcon{Icon=Icon,Text="内容迁移 · 手机接收",Visible=true};
            var menu=new ContextMenuStrip();menu.Items.Add("连接手机 / 配对",null,delegate{Show();Activate();});
            menu.Items.Add("退出手机接收",null,delegate{if(server!=null)server.Dispose();tray.Visible=false;tray.Dispose();Application.Exit();});
            tray.ContextMenuStrip=menu;tray.DoubleClick+=delegate{Show();Activate();};
            enabled.CheckedChanged+=delegate{settings.Enabled=enabled.Checked;RefreshServer();};
            remote.CheckedChanged+=delegate{settings.Remote=remote.Checked;ReloadAddresses();RefreshServer();};
            addresses.SelectedIndexChanged+=delegate{if(loading)return;RememberAddress();RefreshServer();};
            Shown+=delegate{ReloadAddresses();RefreshServer();};
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
        }
        void RememberAddress(){if(addresses.SelectedItem==null)return;if(settings.Remote)settings.RemoteAddress=addresses.SelectedItem.ToString();else settings.LocalAddress=addresses.SelectedItem.ToString();}
        void ReloadAddresses(){
            loading=true;
            try{
                string prior=settings.Remote?settings.RemoteAddress:settings.LocalAddress;
                addresses.Items.Clear();
                foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces()){
                    if(adapter.OperationalStatus!=OperationalStatus.Up || adapter.NetworkInterfaceType==NetworkInterfaceType.Loopback)continue;
                    bool tailscale=(adapter.Name+" "+adapter.Description).IndexOf("Tailscale",StringComparison.OrdinalIgnoreCase)>=0;
                    try{foreach(var address in adapter.GetIPProperties().UnicastAddresses){
                        if(address.Address.AddressFamily!=AddressFamily.InterNetwork)continue;
                        bool match=settings.Remote ? tailscale && MobileServer.Tailscale(address.Address) : MobileServer.Private(address.Address);
                        string value=address.Address.ToString();if(match && !addresses.Items.Contains(value))addresses.Items.Add(value);
                    }}catch(NetworkInformationException){}
                }
                if(addresses.Items.Contains(prior))addresses.SelectedItem=prior;else if(addresses.Items.Count>0)addresses.SelectedIndex=0;
                RememberAddress();
            }finally{loading=false;}
        }
        void DrawCode(){
            if(qr.Image!=null){var old=qr.Image;qr.Image=null;old.Dispose();}
            if(server==null || addresses.SelectedItem==null)return;
            string uri="contentmover://pair?v=1&host="+addresses.SelectedItem+"&port=47831&key="+Uri.EscapeDataString(settings.Key)+(settings.Remote?"&mode=remote":"");
            var writer=new ZXing.BarcodeWriter{Format=ZXing.BarcodeFormat.QR_CODE,Options=new ZXing.Common.EncodingOptions{Width=300,Height=300,Margin=2}};
            qr.Image=writer.Write(uri);
        }
        void RefreshServer(){
            if(server!=null){server.Dispose();server=null;}
            File.WriteAllText(Path.Combine(stateDirectory,"settings.json"),new JavaScriptSerializer().Serialize(settings));
            if(!settings.Enabled){status.Text="接收已关闭。\n文件按电脑接收日期保存到桌面，旧文件不会被改写。";DrawCode();return;}
            if(addresses.SelectedItem==null){status.Text=settings.Remote ? "尚未找到 Tailscale 地址。\n请安装并连接 Tailscale，手机登录同一账号。\n连接后点“刷新地址”，再用手机内容迁移扫码。" : "尚未找到局域网地址。请连接 Wi-Fi 或手机热点，再点“刷新地址”。";DrawCode();return;}
            try{
                server=new MobileServer(Convert.FromBase64String(settings.Key),new MobileStore(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),stateDirectory),settings.Remote);
                server.Saved+=delegate(string output){try{BeginInvoke(new Action(delegate{status.Text="已保存："+Path.GetFileName(output);tray.BalloonTipTitle="收到手机内容";tray.BalloonTipText=Path.GetFileName(output);tray.ShowBalloonTip(3000);}));}catch{}};
                // Remote mode listens only on the selected virtual interface, never a public adapter.
                server.Start(47831,settings.Remote ? IPAddress.Parse(addresses.SelectedItem.ToString()) : IPAddress.Any);
                status.Text=settings.Remote ? "远程接收已开启，手机内容迁移扫码连接。\n手机用流量、电脑用 Wi-Fi 均可，需两端保持 Tailscale 连接。\n电脑需开机且不休眠，手机接收程序保持运行。" : "局域网接收已开启，手机内容迁移扫码连接。\n两台设备连接同一 Wi-Fi 或手机热点。";
            }catch(Exception error){if(server!=null)server.Dispose();server=null;status.Text="接收未开启："+error.Message;}
            DrawCode();
        }
        protected override void OnFormClosed(FormClosedEventArgs e){if(server!=null)server.Dispose();if(qr.Image!=null)qr.Image.Dispose();tray.Dispose();base.OnFormClosed(e);}
    }
}
