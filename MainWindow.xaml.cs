using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;
using Microsoft.Win32;
using System.Text.Json;
using System.Diagnostics;
using System.Windows.Documents;

namespace SpeechDatasetMaker;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>

public class datasetConfig
{
  public string LangCode { get; set; }
  public string LangTitle { get; set; }
  public string TextDirection { get; set; }
  public string SentencesFile { get; set; }
  public string OutputDir { get; set; }
  public string WavsDir { get; set; }
  public int SampleRate { get; set; }
  public int BitDepth { get; set; }
  public int Channels { get; set; }
  public string Speaker { get; set; }
  public string Gender { get; set; }
}

public partial class MainWindow : Window
{
  private Dictionary<string, string> sentences = new Dictionary<string, string>();
  private List<string> existingSentenceIDs = new List<string>();
  private WaveInEvent waveIn;
  private WaveFileWriter writer;
  private WaveOutEvent waveOut;
  private datasetConfig conf;
  private string tempFile;

  public float threshold = 0.05f; // Adjust silence threshold as needed

  public MainWindow()
  {
    InitializeComponent();
    LoadMicrophones();
    LoadDatasets();
  }

  private void LoadMicrophones()
  {
    for (int i = 0; i < WaveIn.DeviceCount; i++)
      MicCombo.Items.Add($"{i}: {WaveIn.GetCapabilities(i).ProductName}");
    if (MicCombo.Items.Count > 0) MicCombo.SelectedIndex = 0;
  }

  private void LoadDatasets()
  {
    var datasets = new List<string>();
    foreach (var file in Directory.GetFiles("datasets"))
    {
      if (Path.GetExtension(file) == ".json")
      {
        var title = Path.GetFileNameWithoutExtension(file);
        datasets.Add(title);
        DatasetCombo.Items.Add(title);
      }
    }
  }
  private void OpenFolder_Click(object sender, RoutedEventArgs e)
  {
    Process.Start("explorer.exe", "datasets");
  }
  // when the user selects a dataset, load the sentences from that dataset
  private void DatasetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    if (DatasetCombo.SelectedIndex == -1) return;
    var dataset = DatasetCombo.SelectedItem.ToString();
    LoadSentences(dataset);
  }

  private void LoadSentences(string dataset)
  {
    sentences.Clear();
    SentenceText.Text = "";
    StatusText.Text = "";

    var path = Path.Combine("datasets", dataset + ".json");
    var jsonString = File.ReadAllText(path);
    if (jsonString.Length > 0)
    {
      conf = JsonSerializer.Deserialize<datasetConfig>(jsonString);
      if (conf.TextDirection == "rtl")
        SentenceText.FlowDirection = FlowDirection.RightToLeft;
      else
        SentenceText.FlowDirection = FlowDirection.LeftToRight;
    }
    else
    {
      MessageBox.Show("config.json file is empty");
      return;
    }

    // if the directory /output does not exist, create it
    if (!Directory.Exists(conf.OutputDir))
      Directory.CreateDirectory(conf.OutputDir);

    if (!Directory.Exists(conf.WavsDir))
      Directory.CreateDirectory(conf.WavsDir);

    // check existing files in the directory /output
    var existing = Directory.GetFiles(conf.WavsDir, "*.wav");
    foreach (var file in existing)
    {
      existingSentenceIDs.Add(Path.GetFileNameWithoutExtension(file));
    }

    if (File.Exists(conf.SentencesFile))
    {
      foreach (var line in File.ReadAllLines(conf.SentencesFile))
      {
        var parts = line.Split('\t');
        if (parts.Length >= 2)
          if (!sentences.ContainsKey(parts[0]) && !existingSentenceIDs.Contains(parts[0]))
            sentences.Add(parts[0], parts[1]);
      }
    }

    // if the Metadata.csv file not exists, create it
    var MetadataFile = Path.Combine(conf.OutputDir, "Metadata.csv");
    if (!File.Exists(MetadataFile))
      using (var writer = new StreamWriter(MetadataFile, false, Encoding.UTF8)) { }

    ShowCurrentSentence();
  }

  private void ShowCurrentSentence()
  {
    if (sentences.Count > 0)
    {
      var currentID = sentences.First();
      SentenceText.Text = currentID.Value;
      StatusText.Text = $"{currentID.Key}";
    }
    else
    {
      SentenceText.Text = "----NO SENTENCE REMAINING----";
      StatusText.Text = "--------";
      RecordBtn.IsEnabled = false;
      StopBtn.IsEnabled = false;
      PlayBtn.IsEnabled = false;
      SaveBtn.IsEnabled = false;

    }
  }

  private void Record_Click(object sender, RoutedEventArgs e)
  {
    if (MicCombo.SelectedIndex == -1) return;

    waveIn = new WaveInEvent
    {
      DeviceNumber = MicCombo.SelectedIndex,
      WaveFormat = new WaveFormat(conf.SampleRate, conf.BitDepth, conf.Channels)
    };
    tempFile = Path.GetTempFileName();
    writer = new WaveFileWriter(tempFile, waveIn.WaveFormat);

    waveIn.DataAvailable += (s, args) => writer.Write(args.Buffer, 0, args.BytesRecorded);
    waveIn.RecordingStopped += (s, args) =>
    {
      writer?.Dispose();
      waveIn?.Dispose();
      Dispatcher.Invoke(() =>
          {
            RecordBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            PlayBtn.IsEnabled = true;
            SaveBtn.IsEnabled = true;
          });
    };

    waveIn.StartRecording();
    RecordBtn.IsEnabled = false;
    StopBtn.IsEnabled = true;
    PlayBtn.IsEnabled = false;
    SaveBtn.IsEnabled = false;
  }

  private void Stop_Click(object sender, RoutedEventArgs e)
  {
    waveIn?.StopRecording();
  }

  private void Play_Click(object sender, RoutedEventArgs e)
  {
    if (string.IsNullOrEmpty(tempFile)) return;

    waveOut?.Stop();
    waveOut?.Dispose();

    waveOut = new WaveOutEvent();
    var reader = new AudioFileReader(tempFile);
    waveOut.Init(reader);
    waveOut.PlaybackStopped += (s, args) => reader.Dispose();
    waveOut.Play();
  }

  private void Save_Click(object sender, RoutedEventArgs e)
  {
    if (string.IsNullOrEmpty(tempFile) || sentences.Count == 0) return;
    var trimmedFile = Path.GetTempFileName() + "_trimmed";
    TrimSilenceAndSave(tempFile, trimmedFile);
    var currentID = sentences.First();
    var path = Path.Combine(conf.WavsDir, $"{currentID.Key}.wav");
    File.Copy(trimmedFile, path, true);
    // append this line to the Metadata.csv file
    var MetadataFile = Path.Combine(conf.OutputDir, "Metadata.csv");
    using (var writer = new StreamWriter(MetadataFile, true, Encoding.UTF8))
    {
      writer.WriteLine($"{currentID.Key}.wav|{SentenceText.Text}");
    }
    sentences.Remove(currentID.Key);
    ShowCurrentSentence();
    PlayBtn.IsEnabled = false;
    SaveBtn.IsEnabled = false;
  }

  private void TrimSilenceAndSave(string inputFile, string outputFile)
  {
    using (var reader = new AudioFileReader(inputFile))
    {
      var samples = new float[reader.Length / 4]; // 4 bytes per float
      reader.Read(samples, 0, samples.Length);


      // Find start of audio (first non-silent sample)
      int startIndex = 0;
      for (int i = 0; i < samples.Length; i++)
      {
        if (Math.Abs(samples[i]) > threshold)
        {
          startIndex = i;
          break;
        }
      }
      startIndex = (startIndex > 150) ? startIndex - 150 : 0; // add a little buffer if possible

      // Find end of audio (last non-silent sample)
      int endIndex = samples.Length - 1;
      for (int i = samples.Length - 1; i >= 0; i--)
      {
        if (Math.Abs(samples[i]) > threshold)
        {
          endIndex = i;
          break;
        }
      }

      endIndex = (endIndex < samples.Length - 150) ? endIndex + 150 : samples.Length - 1; // add a little buffer if possible

      // Create trimmed audio
      if (startIndex < endIndex)
      {
        int trimmedLength = endIndex - startIndex + 1;
        float[] trimmedSamples = new float[trimmedLength];
        Array.Copy(samples, startIndex, trimmedSamples, 0, trimmedLength);

        // Save trimmed audio
        using (var writer = new WaveFileWriter(outputFile, reader.WaveFormat))
        {
          writer.WriteSamples(trimmedSamples, 0, trimmedSamples.Length);
        }
      }
      else
      {
        // If no audio detected, save original file
        File.Copy(inputFile, outputFile, true);
      }
    }
  }

  private void OutFolder_Click(object sender, RoutedEventArgs e)
  {
    if (conf != null)
      Process.Start("explorer.exe", $"\"{conf.OutputDir.Replace("/", "\\")}\"");
  }
}