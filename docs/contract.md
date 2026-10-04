# Campfire route and channel contract

Source of truth: Rails `config/routes.rb`, `app/channels/`, and `db/schema.rb` under `/tmp/once-campfire.hapg88/once-campfire` (read-only). Not a `rails routes` dump. HTTP routes below are the Rails resource expansion of `config/routes.rb` (PATCH and PUT listed separately). Direct URL helpers are not HTTP routes and are not in the route count.

`nested` is Rails `ActionDispatch::Routing::Mapper#nested`. Inside `resources :rooms` it applies the parent member scope (`/rooms/:room_id`, name prefix `room`) and does not add a segment of its own. The inner `scope path: ":bot_key"` adds the bot key. Bot helper arity in app code is `room_bot_messages_url(room, bot_key)`.

Optional `(.:format)` applies to ordinary routes unless noted.

## Routes (151)

| # | Method | Path | Controller#action | Name |
|---|--------|------|-------------------|------|
| 1 | GET | `/` | `welcome#show` | `root` |
| 2 | GET | `/first_run/new` | `first_runs#new` | `new_first_run` |
| 3 | POST | `/first_run` | `first_runs#create` | `first_run` |
| 4 | GET | `/first_run` | `first_runs#show` | `first_run` |
| 5 | GET | `/first_run/edit` | `first_runs#edit` | `edit_first_run` |
| 6 | PATCH | `/first_run` | `first_runs#update` | `first_run` |
| 7 | PUT | `/first_run` | `first_runs#update` | `first_run` |
| 8 | DELETE | `/first_run` | `first_runs#destroy` | `first_run` |
| 9 | GET | `/session/new` | `sessions#new` | `new_session` |
| 10 | POST | `/session` | `sessions#create` | `session` |
| 11 | GET | `/session` | `sessions#show` | `session` |
| 12 | GET | `/session/edit` | `sessions#edit` | `edit_session` |
| 13 | PATCH | `/session` | `sessions#update` | `session` |
| 14 | PUT | `/session` | `sessions#update` | `session` |
| 15 | DELETE | `/session` | `sessions#destroy` | `session` |
| 16 | GET | `/session/transfers/:id` | `sessions/transfers#show` | `session_transfer` |
| 17 | PATCH | `/session/transfers/:id` | `sessions/transfers#update` | `session_transfer` |
| 18 | PUT | `/session/transfers/:id` | `sessions/transfers#update` | `session_transfer` |
| 19 | GET | `/account/new` | `accounts#new` | `new_account` |
| 20 | POST | `/account` | `accounts#create` | `account` |
| 21 | GET | `/account` | `accounts#show` | `account` |
| 22 | GET | `/account/edit` | `accounts#edit` | `edit_account` |
| 23 | PATCH | `/account` | `accounts#update` | `account` |
| 24 | PUT | `/account` | `accounts#update` | `account` |
| 25 | DELETE | `/account` | `accounts#destroy` | `account` |
| 26 | GET | `/account/users` | `accounts/users#index` | `account_users` |
| 27 | POST | `/account/users` | `accounts/users#create` | `account_users` |
| 28 | GET | `/account/users/new` | `accounts/users#new` | `new_account_user` |
| 29 | GET | `/account/users/:id` | `accounts/users#show` | `account_user` |
| 30 | GET | `/account/users/:id/edit` | `accounts/users#edit` | `edit_account_user` |
| 31 | PATCH | `/account/users/:id` | `accounts/users#update` | `account_user` |
| 32 | PUT | `/account/users/:id` | `accounts/users#update` | `account_user` |
| 33 | DELETE | `/account/users/:id` | `accounts/users#destroy` | `account_user` |
| 34 | GET | `/account/bots` | `accounts/bots#index` | `account_bots` |
| 35 | POST | `/account/bots` | `accounts/bots#create` | `account_bots` |
| 36 | GET | `/account/bots/new` | `accounts/bots#new` | `new_account_bot` |
| 37 | GET | `/account/bots/:id` | `accounts/bots#show` | `account_bot` |
| 38 | GET | `/account/bots/:id/edit` | `accounts/bots#edit` | `edit_account_bot` |
| 39 | PATCH | `/account/bots/:id` | `accounts/bots#update` | `account_bot` |
| 40 | PUT | `/account/bots/:id` | `accounts/bots#update` | `account_bot` |
| 41 | DELETE | `/account/bots/:id` | `accounts/bots#destroy` | `account_bot` |
| 42 | PATCH | `/account/bots/:bot_id/key` | `accounts/bots/keys#update` | `account_bot_key` |
| 43 | PUT | `/account/bots/:bot_id/key` | `accounts/bots/keys#update` | `account_bot_key` |
| 44 | POST | `/account/join_code` | `accounts/join_codes#create` | `account_join_code` |
| 45 | GET | `/account/logo` | `accounts/logos#show` | `account_logo` |
| 46 | DELETE | `/account/logo` | `accounts/logos#destroy` | `account_logo` |
| 47 | GET | `/account/custom_styles/edit` | `accounts/custom_styles#edit` | `edit_account_custom_styles` |
| 48 | PATCH | `/account/custom_styles` | `accounts/custom_styles#update` | `account_custom_styles` |
| 49 | PUT | `/account/custom_styles` | `accounts/custom_styles#update` | `account_custom_styles` |
| 50 | GET | `/join/:join_code` | `users#new` | `join` |
| 51 | POST | `/join/:join_code` | `users#create` | (none; `as:` only on the GET) |
| 52 | GET | `/qr_code/:id` | `qr_code#show` | `qr_code` |
| 53 | GET | `/users/:id` | `users#show` | `user` |
| 54 | GET | `/users/:user_id/avatar` | `users/avatars#show` | `user_avatar` |
| 55 | DELETE | `/users/:user_id/avatar` | `users/avatars#destroy` | `user_avatar` |
| 56 | POST | `/users/:user_id/ban` | `users/bans#create` | `user_ban` |
| 57 | DELETE | `/users/:user_id/ban` | `users/bans#destroy` | `user_ban` |
| 58 | GET | `/users/:user_id/sidebar` | `users/sidebars#show` | `user_sidebar` |
| 59 | GET | `/users/:user_id/profile/new` | `users/profiles#new` | `new_user_profile` |
| 60 | POST | `/users/:user_id/profile` | `users/profiles#create` | `user_profile` |
| 61 | GET | `/users/:user_id/profile` | `users/profiles#show` | `user_profile` |
| 62 | GET | `/users/:user_id/profile/edit` | `users/profiles#edit` | `edit_user_profile` |
| 63 | PATCH | `/users/:user_id/profile` | `users/profiles#update` | `user_profile` |
| 64 | PUT | `/users/:user_id/profile` | `users/profiles#update` | `user_profile` |
| 65 | DELETE | `/users/:user_id/profile` | `users/profiles#destroy` | `user_profile` |
| 66 | GET | `/users/:user_id/push_subscriptions` | `users/push_subscriptions#index` | `user_push_subscriptions` |
| 67 | POST | `/users/:user_id/push_subscriptions` | `users/push_subscriptions#create` | `user_push_subscriptions` |
| 68 | GET | `/users/:user_id/push_subscriptions/new` | `users/push_subscriptions#new` | `new_user_push_subscription` |
| 69 | GET | `/users/:user_id/push_subscriptions/:id` | `users/push_subscriptions#show` | `user_push_subscription` |
| 70 | GET | `/users/:user_id/push_subscriptions/:id/edit` | `users/push_subscriptions#edit` | `edit_user_push_subscription` |
| 71 | PATCH | `/users/:user_id/push_subscriptions/:id` | `users/push_subscriptions#update` | `user_push_subscription` |
| 72 | PUT | `/users/:user_id/push_subscriptions/:id` | `users/push_subscriptions#update` | `user_push_subscription` |
| 73 | DELETE | `/users/:user_id/push_subscriptions/:id` | `users/push_subscriptions#destroy` | `user_push_subscription` |
| 74 | POST | `/users/:user_id/push_subscriptions/:push_subscription_id/test_notifications` | `users/push_subscriptions/test_notifications#create` | `user_push_subscription_test_notifications` |
| 75 | GET | `/autocompletable/users` | `autocompletable/users#index` | `autocompletable_users` |
| 76 | GET | `/rooms` | `rooms#index` | `rooms` |
| 77 | POST | `/rooms` | `rooms#create` | `rooms` |
| 78 | GET | `/rooms/new` | `rooms#new` | `new_room` |
| 79 | GET | `/rooms/:id` | `rooms#show` | `room` |
| 80 | GET | `/rooms/:id/edit` | `rooms#edit` | `edit_room` |
| 81 | PATCH | `/rooms/:id` | `rooms#update` | `room` |
| 82 | PUT | `/rooms/:id` | `rooms#update` | `room` |
| 83 | DELETE | `/rooms/:id` | `rooms#destroy` | `room` |
| 84 | GET | `/rooms/:room_id/messages` | `messages#index` | `room_messages` |
| 85 | POST | `/rooms/:room_id/messages` | `messages#create` | `room_messages` |
| 86 | GET | `/rooms/:room_id/messages/new` | `messages#new` | `new_room_message` |
| 87 | GET | `/rooms/:room_id/messages/:id` | `messages#show` | `room_message` |
| 88 | GET | `/rooms/:room_id/messages/:id/edit` | `messages#edit` | `edit_room_message` |
| 89 | PATCH | `/rooms/:room_id/messages/:id` | `messages#update` | `room_message` |
| 90 | PUT | `/rooms/:room_id/messages/:id` | `messages#update` | `room_message` |
| 91 | DELETE | `/rooms/:room_id/messages/:id` | `messages#destroy` | `room_message` |
| 92 | GET | `/rooms/:room_id/:bot_key/messages` | `messages/by_bots#index` | `room_bot_messages` |
| 93 | POST | `/rooms/:room_id/:bot_key/messages` | `messages/by_bots#create` | `room_bot_messages` |
| 94 | PATCH | `/rooms/:room_id/:bot_key/messages/:id` | `messages/by_bots#update` | `room_bot_message` |
| 95 | PUT | `/rooms/:room_id/:bot_key/messages/:id` | `messages/by_bots#update` | `room_bot_message` |
| 96 | DELETE | `/rooms/:room_id/:bot_key/messages/:id` | `messages/by_bots#destroy` | `room_bot_message` |
| 97 | POST | `/rooms/:room_id/:bot_key/messages/:message_id/boosts` | `messages/boosts/by_bots#create` | `room_bot_message_boosts` |
| 98 | DELETE | `/rooms/:room_id/:bot_key/messages/:message_id/boosts/:id` | `messages/boosts/by_bots#destroy` | `room_bot_message_boost` |
| 99 | GET | `/rooms/:room_id/refresh` | `rooms/refreshes#show` | `room_refresh` |
| 100 | GET | `/rooms/:room_id/settings` | `rooms/settings#show` | `room_settings` |
| 101 | GET | `/rooms/:room_id/involvement` | `rooms/involvements#show` | `room_involvement` |
| 102 | PATCH | `/rooms/:room_id/involvement` | `rooms/involvements#update` | `room_involvement` |
| 103 | PUT | `/rooms/:room_id/involvement` | `rooms/involvements#update` | `room_involvement` |
| 104 | GET | `/rooms/:id/@:message_id` | `rooms#show` | `room_at_message` |
| 105 | GET | `/rooms/opens` | `rooms/opens#index` | `rooms_opens` |
| 106 | POST | `/rooms/opens` | `rooms/opens#create` | `rooms_opens` |
| 107 | GET | `/rooms/opens/new` | `rooms/opens#new` | `new_rooms_open` |
| 108 | GET | `/rooms/opens/:id` | `rooms/opens#show` | `rooms_open` |
| 109 | GET | `/rooms/opens/:id/edit` | `rooms/opens#edit` | `edit_rooms_open` |
| 110 | PATCH | `/rooms/opens/:id` | `rooms/opens#update` | `rooms_open` |
| 111 | PUT | `/rooms/opens/:id` | `rooms/opens#update` | `rooms_open` |
| 112 | DELETE | `/rooms/opens/:id` | `rooms/opens#destroy` | `rooms_open` |
| 113 | GET | `/rooms/closeds` | `rooms/closeds#index` | `rooms_closeds` |
| 114 | POST | `/rooms/closeds` | `rooms/closeds#create` | `rooms_closeds` |
| 115 | GET | `/rooms/closeds/new` | `rooms/closeds#new` | `new_rooms_closed` |
| 116 | GET | `/rooms/closeds/:id` | `rooms/closeds#show` | `rooms_closed` |
| 117 | GET | `/rooms/closeds/:id/edit` | `rooms/closeds#edit` | `edit_rooms_closed` |
| 118 | PATCH | `/rooms/closeds/:id` | `rooms/closeds#update` | `rooms_closed` |
| 119 | PUT | `/rooms/closeds/:id` | `rooms/closeds#update` | `rooms_closed` |
| 120 | DELETE | `/rooms/closeds/:id` | `rooms/closeds#destroy` | `rooms_closed` |
| 121 | GET | `/rooms/directs` | `rooms/directs#index` | `rooms_directs` |
| 122 | POST | `/rooms/directs` | `rooms/directs#create` | `rooms_directs` |
| 123 | GET | `/rooms/directs/new` | `rooms/directs#new` | `new_rooms_direct` |
| 124 | GET | `/rooms/directs/:id` | `rooms/directs#show` | `rooms_direct` |
| 125 | GET | `/rooms/directs/:id/edit` | `rooms/directs#edit` | `edit_rooms_direct` |
| 126 | PATCH | `/rooms/directs/:id` | `rooms/directs#update` | `rooms_direct` |
| 127 | PUT | `/rooms/directs/:id` | `rooms/directs#update` | `rooms_direct` |
| 128 | DELETE | `/rooms/directs/:id` | `rooms/directs#destroy` | `rooms_direct` |
| 129 | GET | `/messages` | `messages#index` | `messages` |
| 130 | POST | `/messages` | `messages#create` | `messages` |
| 131 | GET | `/messages/new` | `messages#new` | `new_message` |
| 132 | GET | `/messages/:id` | `messages#show` | `message` |
| 133 | GET | `/messages/:id/edit` | `messages#edit` | `edit_message` |
| 134 | PATCH | `/messages/:id` | `messages#update` | `message` |
| 135 | PUT | `/messages/:id` | `messages#update` | `message` |
| 136 | DELETE | `/messages/:id` | `messages#destroy` | `message` |
| 137 | GET | `/messages/:message_id/boosts` | `messages/boosts#index` | `message_boosts` |
| 138 | POST | `/messages/:message_id/boosts` | `messages/boosts#create` | `message_boosts` |
| 139 | GET | `/messages/:message_id/boosts/new` | `messages/boosts#new` | `new_message_boost` |
| 140 | GET | `/messages/:message_id/boosts/:id` | `messages/boosts#show` | `message_boost` |
| 141 | GET | `/messages/:message_id/boosts/:id/edit` | `messages/boosts#edit` | `edit_message_boost` |
| 142 | PATCH | `/messages/:message_id/boosts/:id` | `messages/boosts#update` | `message_boost` |
| 143 | PUT | `/messages/:message_id/boosts/:id` | `messages/boosts#update` | `message_boost` |
| 144 | DELETE | `/messages/:message_id/boosts/:id` | `messages/boosts#destroy` | `message_boost` |
| 145 | GET | `/searches` | `searches#index` | `searches` |
| 146 | POST | `/searches` | `searches#create` | `searches` |
| 147 | DELETE | `/searches/clear` | `searches#clear` | `clear_searches` |
| 148 | POST | `/unfurl_link` | `unfurl_links#create` | `unfurl_link` |
| 149 | GET | `/webmanifest` | `pwa#manifest` | `webmanifest` |
| 150 | GET | `/service-worker` | `pwa#service_worker` | `service_worker` |
| 151 | GET | `/up` | `rails/health#show` | `rails_health_check` |

### Defaults and scopes (from `config/routes.rb`)

- Singular `resource` routes (no `:id`): `first_run`, `session`, `account`, and the nested singular resources under them (`key`, `join_code`, `logo`, `custom_styles`, `avatar`, `ban`, `sidebar`, `profile`, `unfurl_link`). Plural `resources` keep an `:id` (or parent `:_id`) segment. `resources :qr_code` is plural routing with a singular name: path `/qr_code/:id`, controller `qr_code` (not pluralized), helper `qr_code`.
- Routes 58–74: `scope defaults: { user_id: "me" }`. Path still has `:user_id`. URL helpers may omit it and send `me`.
- Routes 92–98: inside `resources :rooms`, `nested` (parent member scope `/rooms/:room_id`), then `scope path: ":bot_key", as: :bot, defaults: { format: :json }`. Controllers are explicit: `messages/by_bots` (index, create, update, destroy only) and `messages/boosts/by_bots` (create, destroy only). Generated URLs use format json. Optional `(.:format)` still applies; the default format is json.
- Route 104: member `get "@:message_id", to: "rooms#show", as: :at_message`. `@` is a literal path segment. Helper `room_at_message`.
- Routes 149–150: no explicit `as:`; names are the path defaults (`service-worker` → `service_worker`). `webmanifest_path` is used by the Rails layout.
- `namespace :rooms` (`opens`, `closeds`, `directs`) is declared after `resources :rooms`. Both sets are listed; this file does not reorder them.
- No Action Cable mount in `config/routes.rb`.

### Direct helpers (not HTTP routes)

- `fresh_account_logo` → `route_for :account_logo` with `v: Current.account&.updated_at` and optional `size`.
- `fresh_user_avatar` → `route_for :user_avatar` with `user.avatar_token` and `v: user.updated_at`.

## Channels

10 files under `app/channels`. 8 channel classes.

- `ApplicationCable::Channel` (`app/channels/application_cable/channel.rb`) — base `ActionCable::Channel::Base`; no `subscribed`.
- `ApplicationCable::Connection` (`app/channels/application_cable/connection.rb`) — not a channel. `identified_by :current_user`. `connect` sets `current_user` from `find_session_by_cookie`, else `reject_unauthorized_connection`. Includes `Authentication::SessionLookup`.
- `HeartbeatChannel` (`app/channels/heartbeat_channel.rb`) — empty subclass; no `subscribed`.
- `RoomChannel` (`app/channels/room_channel.rb`) — `subscribed`: `stream_for` the room from `current_user.rooms.find_by(id: params[:room_id])`, else `reject`.
- `PresenceChannel` (`app/channels/presence_channel.rb`) — subclasses `RoomChannel` (same room `stream_for`). `on_subscribe` calls `present` (`membership.present`, then broadcasts `{ room_id }` to `user_#{current_user.id}_reads`) unless rejected. `on_unsubscribe` calls `absent` (`membership.disconnected`). `refresh` calls `membership.refresh_connection`.
- `TypingNotificationsChannel` (`app/channels/typing_notifications_channel.rb`) — subclasses `RoomChannel` (same room `stream_for`). `start`/`stop` `broadcast_to @room` with action and `current_user` id/name.
- `RoomMessagesChannel` (`app/channels/room_messages_channel.rb`) — `subscribed`: `stream_from` the verified stream name when suffix is `messages` and `current_user` is a member of the `Room` located from the GID in that name; else `reject`. Does not take a room id param.
- `ReadRoomsChannel` (`app/channels/read_rooms_channel.rb`) — `subscribed`: `stream_from "user_#{current_user.id}_reads"`.
- `UnreadRoomsChannel` (`app/channels/unread_rooms_channel.rb`) — `subscribed`: `stream_from "user_#{current_user.id}_unreads"` (`stream_name_for`).
- `RoomStreamsAreAuthorized` (`app/channels/concerns/room_streams_are_authorized.rb`) — module, not a channel. `subscribed` rejects when `RoomMessagesChannel.guarded_stream?` is true for the verified stream name, else `super`. File comment: prepended onto `Turbo::StreamsChannel`.

## Auth entry points

- Session cookie lookup concern: `app/controllers/concerns/authentication/session_lookup.rb` (`find_session_by_cookie`; reads `cookies.signed[:session_token]`). Used by `ApplicationCable::Connection` and `Authentication`.
- HTTP auth concern: `app/controllers/concerns/authentication.rb` (`require_authentication` restores the cookie session, else `bot_authentication` via `params[:bot_key]`).
- Session store initializer: `config/initializers/session_store.rb` (`cookie_store`, key `_campfire_session`).
- Bot key route scope (not cookie auth): `config/routes.rb` `scope path: ":bot_key", as: :bot, defaults: { format: :json }` inside `resources :rooms` (routes 92–98).

## Schema

`db/schema.rb` line 13: `ActiveRecord::Schema[8.2].define(version: 2025_12_12_154340) do`

## Counts

- Routes: 151
- Channels: 8 classes (10 files under `app/channels`)
