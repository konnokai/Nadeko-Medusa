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
            var result = await _c.GetImageDataAsync(avatarUrl);
            if (!result.TryPickT0(out var data, out _))
            {
                using var http = _httpFactory.CreateClient();
                data = await http.GetByteArrayAsync(avatarUrl);
                using (var avatarImg = Image.Load<Rgba32>(data))
                {
                    avatarImg.Mutate(x => x.Resize(85, 85).ApplyRoundedCorners(42));
                    await using var avStream = await avatarImg.ToStreamAsync();
                    data = avStream.ToArray();
                    DrawAvatar(bg, avatarImg);
                }

                await _c.SetImageDataAsync(avatarUrl, data);
            }
            else
            {
                using var avatarImg = Image.Load<Rgba32>(data);
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
