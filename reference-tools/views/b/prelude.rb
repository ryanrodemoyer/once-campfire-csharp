# Runs the reference app without Redis: jobs are dropped, cable broadcasts go to the
# in-process async adapter and fragment caching is off.
class DropJobsAdapter
  def enqueue(*) = nil
  def enqueue_at(*) = nil
  def enqueue_after_transaction_commit? = false
end
ActiveJob::Base.queue_adapter = DropJobsAdapter.new
ActionCable.server.config.cable = { "adapter" => "async" }
Rails.cache = ActiveSupport::Cache::NullStore.new
ActionController::Base.cache_store = ActiveSupport::Cache::NullStore.new
