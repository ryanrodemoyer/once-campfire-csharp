# Builds the view-model JSON the Rust views take (crates/views/src/{messages,rooms,searches}),
# from the same records the reference app rendered.
module ViewsB
  module Inputs
    extend self

    def view
      @view ||= ApplicationController.new.tap do |controller|
        controller.request = ActionDispatch::TestRequest.create("HTTP_HOST" => ViewsB::HOST, "rack.url_scheme" => "http")
        controller.response = ActionDispatch::Response.new
      end.view_context
    end

    def time(t) = t.utc.iso8601(9)

    # The ViewContext fields the Rust tests set, as the request the reference served saw them.
    def context(user, session)
      platform = ApplicationPlatform.new(session.request&.user_agent.to_s)
      {
        base_url: "http://#{ViewsB::HOST}",
        current_user: user && { id: user.id, name: user.name, administrator: user.administrator?, bot: user.bot?, avatar_url: view.fresh_user_avatar_path(user) },
        account: { name: Account.first.name, logo_url: view.fresh_account_logo_path, has_logo: Account.first.logo.attached? },
        last_room_visited_id: last_room_visited_id(user, session),
        platform: {
          ios: platform.ios?, android: platform.android?, mac: platform.mac?, windows: platform.windows?,
          chrome: platform.chrome?, firefox: platform.firefox?, safari: platform.safari?, edge: platform.edge?,
          mobile: platform.mobile?, desktop: platform.desktop?, apple_messages: platform.apple_messages?,
          browser: platform.send(:user_agent).browser.to_s, operating_system: platform.operating_system.to_s
        },
        assets: assets
      }
    end

    def last_room_visited_id(user, session)
      return nil unless user && !user.bot?
      id = session.cookies["last_room"]
      (id && user.rooms.find_by(id: id)&.id) || user.rooms.original&.id
    end

    def assets
      @assets ||= Rails.application.assets.load_path.assets
        .map { |asset| asset.logical_path.to_s }
        .select { |path| path.match?(/\.(svg|png|webp|mp3|jpg|gif)\z/) }
        .to_h { |path| [ path, view.asset_path(path) ] }
    end

    def user(user)
      { id: user.id, name: user.name, title: user.title, avatar_url: view.fresh_user_avatar_path(user) }
    end

    def room_kind(room) = room.class.name.demodulize.downcase

    def message(message)
      {
        id: message.id,
        client_message_id: message.client_message_id,
        room_id: message.room_id,
        room_name: view.room_display_name(message.room, for_user: nil),
        creator: user(message.creator),
        created_at: time(message.created_at),
        updated_at: time(message.updated_at),
        all_emoji: message.plain_text_body.all_emoji?,
        content: content(message),
        boosts: message.boosts.ordered.map { |boost| boost(boost) }
      }
    end

    def content(message)
      case message.content_type
      when "attachment" then { type: "attachment" }.merge(attachment(message.attachment))
      when "sound"
        sound = message.sound
        { type: "sound", url: view.asset_path(sound.asset_path),
          image: sound.image && { src: view.image_path(sound.image.asset_path), width: sound.image.width, height: sound.image.height },
          text: sound.text }
      else
        { type: "text", html: view.message_presentation(message).to_s }
      end
    end

    def attachment(attachment)
      preview =
        if attachment.previewable? || attachment.variable?
          if attachment.video?
            { type: "video", poster_url: view.url_for(attachment.preview(format: :webp, resize_to_limit: [ Message::THUMBNAIL_MAX_WIDTH, Message::THUMBNAIL_MAX_HEIGHT ])) }
          else
            { type: "image", thumb_url: view.polymorphic_url(attachment.representation(:thumb), only_path: true) }
          end
        else
          { type: "file" }
        end
      {
        filename: attachment.filename.to_s,
        blob_path: view.rails_blob_path(attachment, only_path: true),
        download_path: view.rails_blob_path(attachment, disposition: "attachment", only_path: true),
        preview: preview,
        width: attachment.metadata[:width],
        height: attachment.metadata[:height]
      }
    end

    def boost(boost)
      { id: boost.id, message_id: boost.message_id, content: boost.content, all_emoji: boost.content.all_emoji?, booster: user(boost.booster) }
    end

    def edit(message)
      { message: message(message), editable_body_html: view.editable_body(message).body.to_html }
    end

    def room(room, for_user)
      { id: room.id, kind: room_kind(room), name: room.name, display_name: view.room_display_name(room, for_user: for_user) }
    end

    def room_show(room, user, session, around: nil)
      messages = room.messages.with_creator.with_attachment_details.with_boosts
      messages = around ? messages.page_around(around) : messages.last_page
      body = session.response.body
      {
        room: room(room, user),
        updated_at: time(room.updated_at),
        user: user(user),
        messages: messages.map { |m| message(m) },
        invitation: room == Room.original && !room.messages.paged?,
        join_code: Account.first.join_code,
        messages_stream_name: body[/channel="RoomMessagesChannel" signed-stream-name="([^"]+)"/, 1]
      }
    end

    def can_administer(user, room) = user.can_administer?(room)

    def open_form(room, user)
      { room: form_room(room), can_administer: user.can_administer?(room), users: User.active.ordered.map { |u| user(u) } }
    end

    def closed_form(room, user)
      selected_ids = room.new_record? ? [] : room.users.pluck(:id)
      selected, unselected = User.active.ordered.partition { |u| selected_ids.include?(u.id) }
      selected, unselected = [], User.active.ordered.to_a if room.new_record?
      {
        room: form_room(room), can_administer: user.can_administer?(room), current_user_id: user.id,
        selected_users: selected.map { |u| user(u) }, unselected_users: unselected.map { |u| user(u) }
      }
    end

    def form_room(room)
      { id: room.new_record? ? nil : room.id, name: room.name }
    end

    def direct_edit(room, user)
      users = room.users.many? ? room.users.without(user) : room.users
      { room_id: room.id, display_name: view.room_display_name(room, for_user: user), users: users.map { |u| user(u) } }
    end

    def involvement(room, user)
      { room_id: room.id, kind: room_kind(room), involvement: room.memberships.find_by(user: user).involvement }
    end

    def refresh(room, since)
      last = Time.at(0, since, :millisecond)
      new_messages = room.messages.with_creator.page_created_since(last)
      updated = room.messages.without(new_messages).with_creator.page_updated_since(last)
      { room_id: room.id, room_kind: room_kind(room), new_messages: new_messages.map { |m| message(m) }, updated_messages: updated.map { |m| message(m) } }
    end

    def search(user, q, session)
      query = q&.gsub(/[^[:word:]]/, " ")
      messages = query.present? ? user.reachable_messages.search(query).last(100) : []
      {
        query: query.presence,
        q: q,
        messages: messages.map { |m| message(m) },
        recent_searches: user.searches.ordered.pluck(:query),
        return_to_room_id: last_room_visited_id(user, session)
      }
    end

    def user_json(user, session)
      { id: user.id, name: user.name, role: user.role, avatar_url: "http://#{ViewsB::HOST}" + view.fresh_user_avatar_path(user) }
    end

    def message_json(message, session)
      message.reload
      {
        id: message.id,
        created_at: message.created_at.utc.as_json,
        body: { plain_text: message.plain_text_body, html: message.body.to_s },
        creator: user_json(message.creator, session),
        room: { id: message.room_id },
        url: "http://#{ViewsB::HOST}/rooms/#{message.room_id}/messages/#{message.id}"
      }
    end

    def boost_json(boost, session)
      {
        id: boost.id, content: boost.content, created_at: boost.created_at.utc.as_json,
        booster: user_json(boost.booster, session),
        message: { id: boost.message_id, url: "http://#{ViewsB::HOST}/rooms/#{boost.message.room_id}/messages/#{boost.message_id}" }
      }
    end
  end
end
