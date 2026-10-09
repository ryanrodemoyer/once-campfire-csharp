<svg version="1.1" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"
  viewBox="0 0 512 512" class="avatar" aria-hidden="true">
  <defs>
    <clipPath id="porthole">
      <circle cx="50%" cy="50%" r="50%" />
    </clipPath>
  </defs>

  <g>
    <rect width="100%" height="100%" rx="50" fill="<%= AvatarBackgroundColor(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) %>" />

    <text x="50%" y="50%" fill="#FFFFFF"
      text-anchor="middle" dy="0.35em"
      <%== user.Initials.Length >= 3 ? "textLength=\"85%\" lengthAdjust=\"spacingAndGlyphs\"" : null %>
      font-family="-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica, Arial, sans-serif"
      font-size="230"
      font-weight="800"
      letter-spacing="-5">
      <%= user.Initials %>
    </text>
  </g>
</svg>
