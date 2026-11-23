using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

internal static class LinkQr {
    public static BitmapImage Render(string url){
        using(var encoder=new QRCodeGenerator())
        using(var data=encoder.CreateQrCode(url,QRCodeGenerator.ECCLevel.M))
        using(var code=new PngByteQRCode(data))
        using(var stream=new MemoryStream(code.GetGraphic(6))){
            var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();return image;
        }
    }
}
