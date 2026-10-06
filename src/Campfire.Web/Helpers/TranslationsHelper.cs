namespace Campfire.Web.Helpers;

// reference/app/helpers/translations_helper.rb
public partial class View
{
    /// <summary><c>TranslationsHelper::TRANSLATIONS</c>: each key's (flag, translation) pairs, in order.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, string>>> Translations { get; } =
        new Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>>(StringComparer.Ordinal)
        {
            ["email_address"] =
            [
                new("🇺🇸", "Enter your email address"),
                new("🇪🇸", "Introduce tu correo electrónico"),
                new("🇫🇷", "Entrez votre adresse courriel"),
                new("🇮🇳", "अपना ईमेल पता दर्ज करें"),
                new("🇩🇪", "Geben Sie Ihre E-Mail-Adresse ein"),
                new("🇧🇷", "Insira seu endereço de email"),
                new("🇯🇵", "メールアドレスを入力してください"),
            ],
            ["password"] =
            [
                new("🇺🇸", "Enter your password"),
                new("🇪🇸", "Introduce tu contraseña"),
                new("🇫🇷", "Saisissez votre mot de passe"),
                new("🇮🇳", "अपना पासवर्ड दर्ज करें"),
                new("🇩🇪", "Geben Sie Ihr Passwort ein"),
                new("🇧🇷", "Insira sua senha"),
                new("🇯🇵", "パスワードを入力してください"),
            ],
            ["update_password"] =
            [
                new("🇺🇸", "Change password"),
                new("🇪🇸", "Cambiar contraseña"),
                new("🇫🇷", "Changer le mot de passe"),
                new("🇮🇳", "पासवर्ड बदलें"),
                new("🇩🇪", "Passwort ändern"),
                new("🇧🇷", "Alterar senha"),
                new("🇯🇵", "パスワードを変更"),
            ],
            ["user_name"] =
            [
                new("🇺🇸", "Enter your name"),
                new("🇪🇸", "Introduce tu nombre"),
                new("🇫🇷", "Entrez votre nom"),
                new("🇮🇳", "अपना नाम दर्ज करें"),
                new("🇩🇪", "Geben Sie Ihren Namen ein"),
                new("🇧🇷", "Insira seu nome"),
                new("🇯🇵", "お名前を入力してください"),
            ],
            ["account_name"] =
            [
                new("🇺🇸", "Name this account"),
                new("🇪🇸", "Nombre de esta cuenta"),
                new("🇫🇷", "Nommez ce compte"),
                new("🇮🇳", "इस खाते का नाम दें"),
                new("🇩🇪", "Benennen Sie dieses Konto"),
                new("🇧🇷", "Dê um nome a essa conta"),
                new("🇯🇵", "アカウントに名前を付ける"),
            ],
            ["room_name"] =
            [
                new("🇺🇸", "Name the room"),
                new("🇪🇸", "Nombrar la sala"),
                new("🇫🇷", "Nommez la salle"),
                new("🇮🇳", "कमरे का नाम दें"),
                new("🇩🇪", "Geben Sie dem Raum einen Namen"),
                new("🇧🇷", "Dê um nome a essa sala"),
                new("🇯🇵", "ルームに名前を付ける"),
            ],
            ["invite_message"] =
            [
                new("🇺🇸", "Welcome to Campfire. To invite some people to chat with you, share the join link below."),
                new("🇪🇸", "Bienvenido a Campfire. Para invitar a algunas personas a chatear contigo, comparte el enlace de unión que se encuentra a continuación."),
                new("🇫🇷", "Bienvenue sur Campfire. Pour inviter des personnes à discuter avec vous, partagez le lien pour rejoindre ci-dessous."),
                new("🇮🇳", "Campfire में आपका स्वागत है। अधिक लोगों को चैट के लिए आमंत्रित करने के लिए, नीचे जुड़ने का लिंक साझा करें।"),
                new("🇩🇪", "Willkommen bei Campfire. Um einige Personen zum Chatten einzuladen, teilen Sie den unten stehenden Beitrittslink."),
                new("🇧🇷", "Boas vindas ao Campfire. Para convidar pessoas para conversarem com você, compartilhe o link de convite abaixo."),
                new("🇯🇵", "Campfireへようこそ。他の人をチャットに招待するには、下記の参加リンクを共有してください。"),
            ],
            ["incompatible_browser_messsage"] =
            [
                new("🇺🇸", "Upgrade to a supported web browser. Campfire requires a modern web browser. Please use one of the browsers listed below and make sure auto-updates are enabled."),
                new("🇪🇸", "Actualiza a un navegador web compatible. Campfire requiere un navegador web moderno. Utiliza uno de los navegadores listados a continuación y asegúrate de que las actualizaciones automáticas estén habilitadas."),
                new("🇫🇷", "Mettez à jour vers un navigateur web pris en charge. Campfire nécessite un navigateur web moderne. Veuillez utiliser l'un des navigateurs répertoriés ci-dessous et assurez-vous que les mises à jour automatiques sont activées."),
                new("🇮🇳", "समर्थित वेब ब्राउज़र में अपग्रेड करें। Campfire को एक आधुनिक वेब ब्राउज़र की आवश्यकता है। कृपया नीचे सूचीबद्ध ब्राउज़रों में से कोई एक का उपयोग करें और सुनिश्चित करें कि स्वचालित अपडेट्स सक्षम हैं।"),
                new("🇩🇪", "Aktualisieren Sie auf einen unterstützten Webbrowser. Campfire erfordert einen modernen Webbrowser. Verwenden Sie bitte einen der unten aufgeführten Browser und stellen Sie sicher, dass automatische Updates aktiviert sind."),
                new("🇧🇷", "Atualize para um navegador compatível. O Campfire requer um navegador moderno. Por favor, use um dos navegadores listados abaixo e certifique-se de que as atualizações automáticas estão ativadas."),
                new("🇯🇵", "サポートされたウェブブラウザーにアップグレードしてください。Campfireはモダンなウェブブラウザーが必要です。下記のブラウザーのいずれかを使用し、自動更新が有効になっていることを確認してください。"),
            ],
            ["bio"] =
            [
                new("🇺🇸", "Enter a few words about yourself."),
                new("🇪🇸", "Ingresa algunas palabras sobre ti mismo."),
                new("🇫🇷", "Saisissez quelques mots à propos de vous-même."),
                new("🇮🇳", "अपने बारे में कुछ शब्द लिखें."),
                new("🇩🇪", "Geben Sie ein paar Worte über sich selbst ein."),
                new("🇧🇷", "Insira alguma palavras sobre você."),
                new("🇯🇵", "ご自分について簡単に記入してください。"),
            ],
            ["webhook_url"] =
            [
                new("🇺🇸", "Webhook URL"),
                new("🇪🇸", "URL del Webhook"),
                new("🇫🇷", "URL du webhook"),
                new("🇮🇳", "वेबहुक URL"),
                new("🇩🇪", "Webhook-URL"),
                new("🇧🇷", "URL do Webhook"),
                new("🇯🇵", "Webhook URL"),
            ],
            ["chat_bots"] =
            [
                new("🇺🇸", "Chat bots. With Chat bots, other sites and services can post updates directly to Campfire."),
                new("🇪🇸", "Bots de chat. Con los bots de chat, otros sitios y servicios pueden publicar actualizaciones directamente en Campfire."),
                new("🇫🇷", "Bots de discussion. Avec les bots de discussion, d'autres sites et services peuvent publier des mises à jour directement sur Campfire."),
                new("🇮🇳", "चैट बॉट। चैट बॉट के साथ, अन्य साइटों और सेवाएं सीधे कैम्पफायर में अपडेट पोस्ट कर सकती हैं।"),
                new("🇩🇪", "Chat-Bots. Mit Chat-Bots können andere Websites und Dienste Updates direkt in Campfire veröffentlichen."),
                new("🇧🇷", "Chat bots. Com Chat bots, outros sites e serviços podem postar atualizações diretamente no Campfire."),
                new("🇯🇵", "チャットボット。チャットボットを使用すると、他のサイトやサービスがCampfireに直接更新情報を投稿できます。"),
            ],
            ["bot_name"] =
            [
                new("🇺🇸", "Name the bot"),
                new("🇪🇸", "Nombrar al bot"),
                new("🇫🇷", "Nommer le bot"),
                new("🇮🇳", "बॉट का नाम दें"),
                new("🇩🇪", "Benenne den Bot"),
                new("🇧🇷", "Dê um nome ao bot"),
                new("🇯🇵", "ボットに名前を付ける"),
            ],
            ["custom_styles"] =
            [
                new("🇺🇸", "Add custom CSS styles. Use Caution: you could break things."),
                new("🇪🇸", "Agrega estilos CSS personalizados. Usa precaución: podrías romper cosas."),
                new("🇫🇷", "Ajoutez des styles CSS personnalisés. Utilisez avec précaution : vous pourriez casser des choses."),
                new("🇮🇳", "कस्टम CSS स्टाइल जोड़ें। सावधानी बरतें: आप चीज़ों को तोड़ सकते हैं।"),
                new("🇩🇪", "Fügen Sie benutzerdefinierte CSS-Stile hinzu. Vorsicht: Sie könnten Dinge kaputt machen."),
                new("🇧🇷", "Adicione estilos CSS personalizados. Use com cuidado: você pode quebrar coisas."),
                new("🇯🇵", "カスタムCSSスタイルを追加。注意: サイトが壊れる可能性があります。"),
            ],
        };

    /// <summary><c>translations_for(translation_key)</c>: a definition list of the translations.</summary>
    public static SafeString TranslationsFor(string translationKey)
    {
        var items = new List<object?>();
        foreach (var (language, translation) in Translations[translationKey])
        {
            items.Add(Tag.Dt(language));
            items.Add(Tag.Dd(translation, new() { { "class", "margin-none" } }));
        }
        return Tag.Dl(OutputSafety.SafeJoin(items), new() { { "class", "language-list" } });
    }

    /// <summary><c>translation_button(translation_key)</c>: the globe button with its popup of translations.</summary>
    public SafeString TranslationButton(string translationKey)
    {
        var summary = Tag.Summary(
            OutputSafety.Concat(
                ImageTag("globe.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } }, { "class", "color-icon" } }),
                Tag.Span("Translate", new() { { "class", "for-screen-reader" } })),
            new() { { "class", "btn" }, { "tabindex", -1 } });
        var menu = Tag.Div(
            TranslationsFor(translationKey),
            new() { { "class", "language-list-menu shadow" }, { "data", new HtmlOptions { { "popup_target", "menu" } } } });
        return Tag.Details(
            OutputSafety.Concat(summary, menu),
            new()
            {
                { "class", "position-relative" },
                {
                    "data", new HtmlOptions
                    {
                        { "controller", "popup" },
                        { "action", "keydown.esc->popup#close toggle->popup#toggle click@document->popup#closeOnClickOutside" },
                        { "popup_orientation_top_class", "popup-orientation-top" },
                    }
                },
            });
    }
}
