# The template digest EtagWithTemplateDigest adds to `Users::AvatarsController#show`'s ETag
# (TEMPLATE_DIGEST in crates/campfire/src/controllers/users/avatars.rs): the SHA256 (truncated)
# of show.svg.erb's source plus "-", since it renders no other templates.
#
#   parity/bin/reference runner reference-tools/campfire/controllers_a/digest.rb
lookup_context = ApplicationController.new.lookup_context
puts ActionView::Digestor.digest(name: "users/avatars/show", format: nil, finder: lookup_context)
puts ActiveSupport::Digest.hexdigest(File.read("app/views/users/avatars/show.svg.erb") + "-")
