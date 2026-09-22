using Nadeko.Common;
using NadekoBot.Extensions;
using NadekoBot.Medusa;
using NadekoBot.Services;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RIP.Service
{
    [svc(Lifetime.Singleton)]
    public class RIPService
    {
        private IImageCache _imgs;
        private IBotCache _c;
        private IHttpClientFactory _httpFactory;

        private List<FontFamily> _fallBackFonts;
        private FontCollection _fonts;
        private FontFamily notoSans;
        private Font _ripFont;

        internal void Inject(IImageCache imageCache, IBotCache botCache, IHttpClientFactory httpClientFactory)
        {
            _imgs = imageCache;
            _c = botCache;
            _httpFactory = httpClientFactory;

            LoadFonts();
        }

        private void LoadFonts()
        {
            _fonts = new();

            notoSans = _fonts.Add("data/fonts/NotoSans-Bold.ttf");

            _fallBackFonts = [];

            // any fonts present in data/fonts should be added as fallback fonts
            // this will allow support for special characters when drawing text
            foreach (var font in Directory.GetFiles(@"data/fonts"))
            {
                if (font.EndsWith(".ttf"))
                    _fallBackFonts.Add(_fonts.Add(font));
                else if (font.EndsWith(".ttc"))
                    _fallBackFonts.AddRange(_fonts.AddCollection(font));
            }

            _ripFont = notoSans.CreateFont(20, FontStyle.Bold);
        }

        internal async Task<Stream> GetRipPictureAsync(string text, Uri imgUrl)
            => (await GetRipPictureFactory(text, imgUrl)).ToStream();

        private void DrawAvatar(Image bg, Image avatarImage)
            => bg.Mutate(x => x.Grayscale().DrawImage(avatarImage, new Point(83, 139), new GraphicsOptions()));

        private async Task<byte[]> GetRipPictureFactory(string text, Uri avatarUrl)
        {
            using var bg = Image.Load<Rgba32>(await _imgs.GetImageDataAsync(new Uri("https://cdn.nadeko.bot/other/rip/rip.png")));
            // cache 只放原始下載內容，縮圖與圓角一律在本地處理。
            // 若把處理後的圖存回原始 URL 這把 key，其他功能取到的頭像會變成 85x85 的縮圖。
            var result = await _c.GetImageDataAsync(avatarUrl);
            if (!result.TryPickT0(out var data, out _))
            {
                using var http = _httpFactory.CreateClient();
                data = await http.GetByteArrayAsync(avatarUrl);
                await _c.SetImageDataAsync(avatarUrl, data);
            }

            using (var avatarImg = Image.Load<Rgba32>(data))
            {
                avatarImg.Mutate(x => x.Resize(85, 85).ApplyRoundedCorners(42));
                DrawAvatar(bg, avatarImg);
            }

            bg.Mutate(x => x.DrawText(
                new RichTextOptions(_ripFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FallbackFontFamilies = _fallBackFonts,
                    Origin = new(bg.Width / 2, 225),
                },
                text,
                Color.Black));

            //flowa
            using (var flowers = Image.Load(await _imgs.GetImageDataAsync(new Uri("https://cdn.nadeko.bot/other/rip/overlay.png"))))
            {
                bg.Mutate(x => x.DrawImage(flowers, new Point(0, 0), new GraphicsOptions()));
            }

            await using var stream = bg.ToStream();
            return stream.ToArray();
        }
    }
}
