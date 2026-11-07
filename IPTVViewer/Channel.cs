namespace IPTVViewer
{
    public class Channel
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Group { get; set; }
        /// <summary>Идентификатор категории на портале (для сопоставления с названием).</summary>
        public string GroupId { get; set; }
        public string TvgId { get; set; }
        public string TvgName { get; set; }
        public string Logo { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Group) ? Name : $"[{Group}] {Name}";
        }
    }
}
