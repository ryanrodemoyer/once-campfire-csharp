{
  "name": "<%= accountName ?? "Campfire" %>",
  "icons": [
    {
      "src": "<%= ManifestAccountLogo(accountUpdatedAt, "small") %>",
      "type": "image/png",
      "sizes": "192x192"
    },
    {
      "src": "<%= ManifestAccountLogo(accountUpdatedAt) %>",
      "type": "image/png",
      "sizes": "512x512"
    },
    {
      "src": "<%= ManifestAccountLogo(accountUpdatedAt) %>",
      "type": "image/png",
      "sizes": "512x512",
      "purpose": "maskable"
    }
  ],
  "start_url": "/",
  "display": "standalone",
  "scope": "/",
  "description": "A chat app from the makers of Basecamp and HEY.",
  "categories": ["social", "business", "productivity"],
  "theme_color": "#ffffff",
  "background_color": "#ffffff",
  "shortcuts": [
    {
      "name": "New chat room",
      "description": "Open Campfire and start a new chat room",
      "url": "rooms/opens/new",
      "icons": [{ "src": "<%= ImageUrl("add.svg") %>", "sizes": "any" }]
    },
    {
      "name": "My profile",
      "description": "Open Campfire and view your profile",
      "url": "/users/me/profile",
      "icons": [{ "src": "<%= ImageUrl("person.svg") %>", "sizes": "any" }]
    }
  ],
  "screenshots": [
    {
      "src": "<%= ImageUrl("screenshots/android-chat.png") %>",
      "sizes": "1080x2400",
      "form_factor": "narrow",
      "label": "Campfire is an installable, self-hosted group chat system."
    },
    {
      "src": "<%= ImageUrl("screenshots/android-sidebar.png") %>",
      "sizes": "1080x2400",
      "form_factor": "narrow",
      "label": "Easily invite people. Make rooms. @mentions, DMs, and mobile support."
    },
    {
      "src": "<%= ImageUrl("screenshots/android-dark-mode.png") %>",
      "sizes": "1080x2400",
      "form_factor": "narrow",
      "label": "Full support for dark mode, customizable to your brand."
    }
  ]
}
