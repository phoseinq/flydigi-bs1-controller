using System.Collections.Generic;
using System.Linq;

namespace Bs1Cooler.App;

/// UI text in English, Persian and Chinese. Keyed by the English text, so XAML stays readable and
/// Retranslate can map any shown string (in any language) to the current one.
public static class Tx
{
    public static string Lang = "en";  // en | fa | zh
    public static readonly string[] Codes = ["en", "fa", "zh"];

    public static string Tr(string en) =>
        Lang == "en" || !T.TryGetValue(en, out var t) ? en : Lang == "fa" ? t.Fa : t.Zh;

    public static string Tr(string en, params object?[] args) => string.Format(Tr(en), args);

    /// A string already on screen, in whatever language, shown in the current one; unknown text is left alone.
    public static string Retranslate(string s) => Back.TryGetValue(s, out var en) ? Tr(en) : s;

    public static bool Known(string s) => Back.ContainsKey(s);

    private static readonly Dictionary<string, (string Fa, string Zh)> T = new()
    {
        // home
        ["Fan"] = ("فن", "风扇"),
        ["CPU"] = ("CPU", "CPU"),
        ["GPU"] = ("GPU", "GPU"),
        ["rpm"] = ("دور", "转/分"),
        ["Smart"] = ("هوشمند", "智能"),
        ["Manual"] = ("دستی", "手动"),
        ["Speed"] = ("سرعت", "转速"),
        ["Follows CPU and GPU temperature. Quieter while media plays."] = ("با دمای CPU و GPU تنظیم می‌شه. موقع پخش فیلم و موسیقی آروم‌تره.", "跟随 CPU 和 GPU 温度。播放媒体时更安静。"),
        ["Lights"] = ("نور", "灯光"),
        ["Color by fan speed"] = ("رنگ بر اساس سرعت فن", "按风扇转速变色"),
        ["Steady color"] = ("رنگ ثابت", "固定颜色"),
        ["Breathe"] = ("تنفسی", "呼吸"),
        ["Wave"] = ("موج", "波浪"),
        ["Twinkle"] = ("چشمک", "闪烁"),
        ["Fade"] = ("محو", "渐变"),
        ["Brightness"] = ("روشنایی", "亮度"),
        ["Color"] = ("رنگ", "颜色"),
        ["Settings"] = ("تنظیمات", "设置"),
        ["Temperatures"] = ("دماها", "温度"),
        ["Disk"] = ("دیسک", "硬盘"),
        ["Motherboard"] = ("مادربرد", "主板"),
        ["Reading sensors…"] = ("در حال خواندن سنسورها…", "正在读取传感器…"),
        // status
        ["Searching…"] = ("در حال جستجو…", "正在搜索…"),
        ["Disconnected"] = ("قطع شده", "已断开"),
        ["Not connected"] = ("وصل نیست", "未连接"),
        ["Powered off (button)"] = ("با دکمه خاموش شده", "已用按钮关闭"),
        ["Off"] = ("خاموش", "已关闭"),
        ["Quiet"] = ("آروم", "安静"),
        ["Heavy load"] = ("بار سنگین", "高负载"),
        ["Media"] = ("پخش", "媒体"),
        ["Call"] = ("تماس", "通话"),
        ["Night"] = ("شب", "夜间"),
        ["Battery"] = ("باتری", "电池"),
        ["Quiet mode"] = ("حالت آروم", "安静模式"),
        ["Slower fan and dimmer lights. Never under heavy load or above 70 °C: cooling always wins."] =
            ("فن آروم‌تر و نور کم‌تر. هیچ‌وقت زیر بار سنگین یا بالای ۷۰ درجه: خنک‌کردن همیشه اولویت داره.", "风扇更慢、灯光更暗。高负载或高于 70 °C 时不生效：散热始终优先。"),
        ["Media and sound"] = ("پخش و صدا", "媒体和声音"),
        ["Any app playing sound for 6 s; ends after 30 s of silence"] = ("هر برنامه‌ای که ۶ ثانیه صدا پخش کنه؛ بعد از ۳۰ ثانیه سکوت تموم می‌شه", "任何应用播放声音 6 秒后开启；安静 30 秒后结束"),
        ["Do Not Disturb"] = ("مزاحم نشوید", "勿扰"),
        ["Calls"] = ("تماس‌ها", "通话"),
        ["While an app uses the microphone"] = ("وقتی برنامه‌ای از میکروفون استفاده می‌کنه", "有应用使用麦克风时"),
        ["Also while presenting"] = ("موقع ارائه هم", "演示时也会开启"),
        ["On battery"] = ("روی باتری", "使用电池时"),
        ["While the laptop is unplugged"] = ("وقتی شارژر لپ‌تاپ وصل نیست", "笔记本未接电源时"),
        ["Every night between these hours"] = ("هر شب بین این ساعت‌ها", "每晚在以下时间段"),
        ["From"] = ("از", "从"),
        ["to"] = ("تا", "到"),
        ["Lights off when locked"] = ("خاموشی نور موقع قفل صفحه", "锁屏时关闭灯光"),
        ["Cooling goes on"] = ("خنک‌کردن ادامه داره", "散热继续工作"),
        // settings
        ["Startup & behavior"] = ("شروع و رفتار", "启动与行为"),
        ["Start with Windows"] = ("اجرا با ویندوز", "随 Windows 启动"),
        ["Runs in the tray when you sign in"] = ("بعد از ورود به ویندوز کنار ساعت اجرا می‌شه", "登录后在托盘中运行"),
        ["When the app opens"] = ("وقتی برنامه باز می‌شه", "应用启动时"),
        ["What the cooler does"] = ("کولر چی کار کنه", "散热器的状态"),
        ["Turn on"] = ("روشن شه", "打开"),
        ["Turn off"] = ("خاموش شه", "关闭"),
        ["Keep as is"] = ("همون‌طور بمونه", "保持不变"),
        ["Off at shutdown and sleep"] = ("خاموش با خاموشی و Sleep", "关机和睡眠时关闭"),
        ["Fan and lights go off with the PC"] = ("فن و نور با کامپیوتر خاموش می‌شن", "风扇和灯光随电脑关闭"),
        ["Quiet while media plays"] = ("بی‌صدا موقع پخش", "播放媒体时静音"),
        ["Slower fan below 70 °C, dimmer lights"] = ("زیر ۷۰ درجه فن آروم‌تر و نور کم‌تر", "70 °C 以下风扇更慢，灯光更暗"),
        ["Cooler"] = ("کولر", "散热器"),
        ["Connect automatically"] = ("اتصال خودکار", "自动连接"),
        ["USB if the cable carries data, else Bluetooth"] = ("اگه کابل دیتا داشته باشه USB، وگرنه بلوتوث", "数据线可用时用 USB，否则用蓝牙"),
        ["Firmware"] = ("فریم‌ور", "固件"),
        ["Update"] = ("آپدیت", "更新"),
        ["Update available"] = ("آپدیت موجوده", "有可用更新"),
        ["Up to date"] = ("به‌روزه", "已是最新"),
        ["Cooler not connected"] = ("کولر وصل نیست", "散热器未连接"),
        ["Official Flydigi {0}"] = ("رسمی Flydigi {0}", "Flydigi 官方 {0}"),
        ["Advanced"] = ("پیشرفته", "高级"),
        ["Edition"] = ("نسخه", "版本"),
        ["Official, ours with more features, or beta for testing"] = ("رسمی، نسخه‌ی ما با امکانات بیشتر، یا بتا برای تست", "官方版、功能更多的我们的版本，或测试用的 Beta"),
        ["Official"] = ("رسمی", "官方"),
        ["Custom"] = ("شخصی", "自定义"),
        ["Beta"] = ("بتا", "Beta"),
        ["Language"] = ("زبان", "语言"),
        ["App language"] = ("زبان برنامه", "应用语言"),
        ["Help & about"] = ("کمک و درباره", "帮助与关于"),
        ["Report a problem"] = ("گزارش مشکل", "报告问题"),
        ["Report"] = ("گزارش", "报告"),
        ["Copies a report and opens the log folder"] = ("یه گزارش کپی می‌کنه و پوشه‌ی لاگ رو باز می‌کنه", "复制一份报告并打开日志文件夹"),
        ["Debug tools"] = ("ابزار دیباگ", "调试工具"),
        ["Open log"] = ("باز کردن لاگ", "打开日志"),
        ["Log all sensors"] = ("ثبت همه‌ی سنسورها در لاگ", "记录所有传感器"),
        ["Copy report"] = ("کپی گزارش", "复制报告"),
        // tray
        ["Open"] = ("باز کردن", "打开"),
        ["Exit"] = ("خروج", "退出"),
        // first run
        ["Welcome"] = ("خوش اومدی", "欢迎"),
        ["BS1 Cooler runs your Flydigi BS1: fan speed from your CPU and GPU temperature, lights, and firmware updates."] =
            ("BS1 Cooler کولر Flydigi BS1 رو کنترل می‌کنه: سرعت فن با دمای CPU و GPU، نورها و آپدیت فریم‌ور.", "BS1 Cooler 控制你的 Flydigi BS1：按 CPU 和 GPU 温度调节风扇、灯光和固件更新。"),
        ["Looking for your cooler…"] = ("دنبال کولر می‌گردم…", "正在查找散热器…"),
        ["Turn it on and plug it into this PC with its USB-C cable, or keep it near the PC for Bluetooth."] =
            ("روشنش کن و با کابل USB-C به همین کامپیوتر وصلش کن، یا برای بلوتوث نزدیک کامپیوتر نگهش دار.", "打开散热器并用 USB-C 线连接到这台电脑，或放在电脑附近使用蓝牙。"),
        ["Found your cooler ({0})"] = ("کولر پیدا شد ({0})", "已找到散热器（{0}）"),
        ["Its firmware ({0}) is out of date. Next: update it, about a minute."] = ("فریم‌ورش ({0}) قدیمیه. مرحله‌ی بعد آپدیتشه، حدود یه دقیقه.", "固件（{0}）已过时。下一步：更新，大约一分钟。"),
        ["Firmware {0}: up to date."] = ("فریم‌ور {0}: به‌روزه.", "固件 {0}：已是最新。"),
        ["Skip"] = ("رد کردن", "跳过"),
        ["Next"] = ("بعدی", "下一步"),
        ["Finish"] = ("تمام", "完成"),
        ["Update firmware"] = ("آپدیت فریم‌ور", "更新固件"),
        // firmware update
        ["Firmware update"] = ("آپدیت فریم‌ور", "固件更新"),
        ["Version {0} of the BS1 Cooler firmware is ready."] = ("نسخه‌ی {0} فریم‌ور BS1 Cooler آماده‌ست.", "BS1 Cooler 固件 {0} 版已就绪。"),
        ["Your cooler's firmware is old: it can't start by itself after a power cut."] = ("فریم‌ور کولرت قدیمیه: بعد از قطع برق خودش روشن نمی‌شه.", "散热器固件过旧：断电后无法自动启动。"),
        ["Updating"] = ("آپدیت", "更新"),
        ["Now on the cooler: {0}."] = ("الان روی کولر: {0}.", "散热器当前固件：{0}。"),
        ["The cooler isn't connected; you can still update it."] = ("کولر وصل نیست، ولی باز هم می‌تونی آپدیتش کنی.", "散热器未连接，但仍可更新。"),
        ["BS1 Cooler firmware (recommended)"] = ("فریم‌ور BS1 Cooler (پیشنهادی)", "BS1 Cooler 固件（推荐）"),
        ["Starts after a power cut, smooth colors, boot and power-down animations, a real off."] =
            ("بعد از قطع برق خودش روشن می‌شه، رنگ‌های نرم، انیمیشن روشن و خاموش شدن، و خاموشی واقعی.", "断电后自动启动、平滑颜色、开关机动画、真正关闭。"),
        ["Official Flydigi firmware"] = ("فریم‌ور رسمی Flydigi", "Flydigi 官方固件"),
        ["The latest from Flydigi, unchanged. Also the way back from ours."] = ("آخرین نسخه‌ی Flydigi بدون تغییر. راه برگشت از نسخه‌ی ما هم همینه.", "Flydigi 最新版，未修改。也可用来恢复官方固件。"),
        ["I understand the cooler restarts during the update. If anything goes wrong, the same steps put the official firmware back."] =
            ("می‌دونم کولر وسط آپدیت ری‌استارت می‌شه. اگه مشکلی پیش بیاد، با همین مراحل فریم‌ور رسمی برمی‌گرده.", "我了解更新时散热器会重启。如果出问题，同样的步骤可以恢复官方固件。"),
        ["Cancel"] = ("لغو", "取消"),
        ["Put the cooler in update mode"] = ("کولر رو ببر تو حالت آپدیت", "让散热器进入更新模式"),
        ["Unplug the cooler's USB-C cable"] = ("کابل USB-C کولر رو بکش", "拔下散热器的 USB-C 线"),
        ["The cable that goes to this PC."] = ("همون کابلی که به این کامپیوتر وصله.", "连接到这台电脑的那根线。"),
        ["Press and hold the power button"] = ("دکمه‌ی پاور رو نگه دار", "按住电源键"),
        ["Keep holding it."] = ("ولش نکن.", "保持按住。"),
        ["Plug the cable back in, then let go"] = ("کابل رو دوباره وصل کن، بعد دکمه رو ول کن", "重新插上线，然后松开"),
        ["The lights stay off: that's update mode."] = ("نورها خاموش می‌مونن: یعنی تو حالت آپدیته.", "灯光保持熄灭：即已进入更新模式。"),
        ["power"] = ("پاور", "电源"),
        ["Not working?"] = ("کار نکرد؟", "不起作用？"),
        ["Use a cable that carries data (not charge-only) and plug it straight into the PC, not a hub. Hold the button before the cable goes in, and keep holding for a second."] =
            ("از کابلی استفاده کن که دیتا داره (نه فقط شارژ) و مستقیم به کامپیوتر وصلش کن، نه به هاب. دکمه رو قبل از وصل کردن کابل نگه دار و یه ثانیه بعدش هم ولش نکن.", "使用可传输数据的线（不是仅充电线），直接插到电脑上，不要用集线器。插线前按住按键，插上后再按住一秒。"),
        ["USB driver needed"] = ("درایور USB لازمه", "需要 USB 驱动"),
        ["Windows found the cooler but has no driver for its update mode."] = ("ویندوز کولر رو پیدا کرد ولی برای حالت آپدیتش درایور نداره.", "Windows 找到了散热器，但没有其更新模式的驱动。"),
        ["Install driver"] = ("نصب درایور", "安装驱动"),
        ["Driver installed"] = ("درایور نصب شد", "驱动已安装"),
        ["Driver install failed"] = ("نصب درایور نشد", "驱动安装失败"),
        ["Unplug the cooler and do the steps again."] = ("کابل کولر رو بکش و مراحل رو دوباره انجام بده.", "拔下散热器，再按步骤操作一次。"),
        ["See the log, or install WCH's CH372 driver by hand."] = ("لاگ رو ببین، یا درایور CH372 شرکت WCH رو دستی نصب کن.", "查看日志，或手动安装 WCH 的 CH372 驱动。"),
        ["Connecting…"] = ("در حال اتصال…", "正在连接…"),
        ["Erasing…"] = ("در حال پاک کردن…", "正在擦除…"),
        ["Writing…"] = ("در حال نوشتن…", "正在写入…"),
        ["Checking…"] = ("در حال بررسی…", "正在校验…"),
        ["Restarting the cooler…"] = ("در حال ری‌استارت کولر…", "正在重启散热器…"),
        ["Don't unplug the cooler. This takes about 10 seconds."] = ("کابل کولر رو نکش. حدود ۱۰ ثانیه طول می‌کشه.", "请勿拔下散热器。大约需要 10 秒。"),
        ["Updated"] = ("آپدیت شد", "已更新"),
        ["That didn't work"] = ("انجام نشد", "未成功"),
        ["Done"] = ("تمام", "完成"),
        ["Close"] = ("بستن", "关闭"),
        ["The cooler restarted with the BS1 Cooler firmware. It reconnects by itself in a few seconds."] =
            ("کولر با فریم‌ور BS1 Cooler ری‌استارت شد. تا چند ثانیه‌ی دیگه خودش دوباره وصل می‌شه.", "散热器已使用 BS1 Cooler 固件重启，几秒后会自动重新连接。"),
        ["The cooler restarted with the official Flydigi firmware. It reconnects by itself in a few seconds."] =
            ("کولر با فریم‌ور رسمی Flydigi ری‌استارت شد. تا چند ثانیه‌ی دیگه خودش دوباره وصل می‌شه.", "散热器已使用 Flydigi 官方固件重启，几秒后会自动重新连接。"),
        ["Unplug the cooler and try again: update mode always works, nothing is lost."] =
            ("کابل کولر رو بکش و دوباره امتحان کن: حالت آپدیت همیشه کار می‌کنه و چیزی از دست نمی‌ره.", "拔下散热器再试一次：更新模式始终可用，不会丢失任何东西。"),
        ["Turning off the cooler"] = ("در حال خاموش کردن کولر", "正在关闭散热器"),
    };

    private static readonly Dictionary<string, string> Back = T
        .SelectMany(kv => new[] { (kv.Key, kv.Key), (kv.Value.Fa, kv.Key), (kv.Value.Zh, kv.Key) })
        .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Item2);
}
