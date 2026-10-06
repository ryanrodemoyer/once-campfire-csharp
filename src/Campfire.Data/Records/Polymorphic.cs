namespace Campfire.Data.Records;

// The `record_type` values Rails writes to polymorphic columns (`action_text_rich_texts` and
// `active_storage_attachments`): the owning model's class name.
public static class RecordTypes
{
    public const string Account = "Account";
    public const string User = Records.User.ModelName;
    public const string Message = Records.Message.ModelName;
    public const string RichText = ActionTextRichText.ModelName;
    public const string Blob = ActiveStorageBlob.ModelName;
    public const string VariantRecord = ActiveStorageVariantRecord.ModelName;
}

// The `name` of each attachment and rich text row, from the models' `has_one_attached`,
// `has_many_attached` and `has_rich_text`.
public static class AttachmentNames
{
    // `Account has_one_attached :logo`
    public const string Logo = "logo";

    // `User has_one_attached :avatar` (user/avatar.rb)
    public const string Avatar = "avatar";

    // `Message has_one_attached :attachment` (message/attachment.rb)
    public const string Attachment = "attachment";

    // `Message has_rich_text :body`
    public const string Body = "body";

    // `ActionText::RichText has_many_attached :embeds`: files in a rich text body.
    public const string Embeds = "embeds";

    // `ActiveStorage::VariantRecord has_one_attached :image`: a processed variant.
    public const string Image = "image";

    // `ActiveStorage::Blob has_one_attached :preview_image`: a video's or PDF's poster.
    public const string PreviewImage = "preview_image";
}
