namespace IPTVViewer
{
    public class Channel
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Group { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Group) ? Name : $"[{Group}] {Name}";
        }
    }
}
