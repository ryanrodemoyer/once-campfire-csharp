# The action callback chains the reference actually runs (order, kind, conditions), for the
# before-action order in crates/campfire/src/concerns.rs.
#
#   parity/bin/reference runner reference-tools/campfire/callbacks.rb
Rails.application.eager_load!
[ApplicationController, ActiveStorage::DirectUploadsController, ActiveStorage::DiskController, ActiveStorage::Blobs::RedirectController, ActiveStorage::Blobs::ProxyController, ActiveStorage::Representations::RedirectController, ActiveStorage::Representations::ProxyController, Rails::HealthController, Turbo::Native::NavigationController, ActionMailbox::Ingresses::Postmark::InboundEmailsController, Rails::Conductor::ActionMailbox::InboundEmailsController].each do |k|
  puts "== #{k} < #{k.superclass}"
  k._process_action_callbacks.each do |cb|
    f = cb.filter
    f = f.is_a?(Proc) ? "proc@#{f.source_location&.join(':')}" : f.inspect
    puts "  #{cb.kind} #{f} if=#{cb.instance_variable_get(:@if).size} unless=#{cb.instance_variable_get(:@unless).size}"
  end
end
puts ActionMailbox.ingress.inspect
puts ActionController::Base.default_protect_from_forgery rescue p $!
