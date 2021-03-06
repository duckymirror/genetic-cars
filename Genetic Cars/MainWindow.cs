﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Genetic_Cars.Car;
using Genetic_Cars.Properties;
using log4net;
// ReSharper disable LocalizableElement
// ReSharper disable RedundantDefaultMemberInitializer

namespace Genetic_Cars
{
  partial class MainWindow : Form
  {
    private static readonly ILog Log = LogManager.GetLogger(
      MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly int PopulationSize =
      Settings.Default.PopulationSize;

    /// <summary>
    /// The id the gui uses to signal that the user wants to watch the lead car.
    /// </summary>
    public const int LeaderCarId = -1;

    /// <summary>
    /// Handles events that use no parameters.
    /// </summary>
    public delegate void GenericHandler();
    
    /// <summary>
    /// Handles the request for a seed change.
    /// </summary>
    /// <param name="seed"></param>
    public delegate void SeedChangedHandler(int seed);

    /// <summary>
    /// Handles a request to kill a car.
    /// </summary>
    /// <param name="id"></param>
    public delegate void KillCarHandler(int id);

    /// <summary>
    /// Handles a request to load a lua script file.
    /// </summary>
    /// <param name="path">The file path</param>
    /// <param name="error">An error message string, if the load fails</param>
    /// <returns>Success/failure of loading.</returns>
    public delegate bool LuaLoadHandler(string path, out string error);
    
    private bool m_paused = false;
    private bool m_graphicsEnabled = true;
    private readonly List<HighScore> m_highScores = new List<HighScore>();
    private int m_followingCarId;
    private string m_lastPopSeed = "";
    private string m_lastTrackSeed = "";

    public MainWindow()
    {
      InitializeComponent();

      var str = toolTip.GetToolTip(popSeedTextBox);
      toolTip.SetToolTip(popSeedLabel, str);
      str = toolTip.GetToolTip(trackSeedTextBox);
      toolTip.SetToolTip(trackSeedLabel, str);
      str = toolTip.GetToolTip(mutationRateTextBox);
      toolTip.SetToolTip(mutationRateLabel, str);
      str = toolTip.GetToolTip(clonesComboBox);
      toolTip.SetToolTip(clonesLabel, str);
      str = toolTip.GetToolTip(randomCarsComboBox);
      toolTip.SetToolTip(randomCarsLabel, str);

      // initialize default values
      mutationRateTextBox.Text = 
        Settings.Default.MutationRate.ToString(
        CultureInfo.CurrentCulture);

      FollowingCarId = LeaderCarId;

      // at most half of the population can be clones, and half can be random
      clonesComboBox.Items.Clear();
      randomCarsComboBox.Items.Clear();
      for (var i = 0; i <= PopulationSize; i++)
      {
        clonesComboBox.Items.Add(i);
        randomCarsComboBox.Items.Add(i);
      }
      clonesComboBox.SelectedIndex = Properties.Settings.Default.NumClones;
      randomCarsComboBox.SelectedIndex = Properties.Settings.Default.NumRandom;

      for (var i = 0; i < PopulationSize; i++)
      {
        var pb = new ColorProgressBar
        {
          Id = i,
          Minimum = 0,
          Maximum = 100,
          Value = 100,
          Text = i.ToString(),
          Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Bold),
          Margin = new Padding(0)
        };
        pb.MouseClick += (sender, args) =>
        {
          if (args.Button == MouseButtons.Left)
          {
            FollowingCarId = ((ColorProgressBar) sender).Id;
          }
        };
        toolTip.SetToolTip(pb, string.Format("Click to view car {0}", i));

        // set a context menu to manually kill cars
        var contextMenu = new ContextMenu();
        contextMenu.Popup += (sender, args) => OnPauseSimulation();
        contextMenu.Collapse += (sender, args) => OnResumeSimulation();
        var menuItem = new MenuItem("Kill");
        menuItem.Click += (sender, args) => OnKillCar(pb.Id);
        contextMenu.MenuItems.Add(menuItem);
        pb.ContextMenu = contextMenu;

        populationList.Controls.Add(pb);
      }
    }

    /// <summary>
    /// Signals that the user has changed the track seed.
    /// </summary>
    public event SeedChangedHandler TrackSeedChanged;

    /// <summary>
    /// Signals that the user has changed the population seed.
    /// </summary>
    public event SeedChangedHandler PopulationSeedChanged;

    /// <summary>
    /// Signals that the user wants to pause the simulation.
    /// </summary>
    public event GenericHandler PauseSimulation;

    /// <summary>
    /// Signals that the user wants to resume the simulation.
    /// </summary>
    public event GenericHandler ResumeSimulation;

    /// <summary>
    /// Signals that the user wants to create a new population.
    /// </summary>
    public event GenericHandler NewPopulation;

    /// <summary>
    /// Signals that the user wants to enable graphics rendering.
    /// </summary>
    public event GenericHandler EnableGraphics;

    /// <summary>
    /// Signals that the user wants to disable the graphics rendering.
    /// </summary>
    public event GenericHandler DisableGraphics;

    /// <summary>
    /// Signals that the user wants to load a lua file.
    /// </summary>
    public event LuaLoadHandler LuaLoad;

    /// <summary>
    /// Signals a request to kill a car manually.
    /// </summary>
    public event KillCarHandler KillCar;

    /// <summary>
    /// The id of the car the user wants the camera to follow.
    /// </summary>
    public int FollowingCarId 
    { get { return m_followingCarId; }
      set
      {
        m_followingCarId = value;
        var status = m_followingCarId == LeaderCarId ? "Yes" : "No";
        followLeaderButton.Text = string.Format("Follow Leader: {0}", status);
      }
    }

    /// <summary>
    /// The handle for the main SFML drawing surface.
    /// </summary>
    public IntPtr DrawingPanelHandle
    {
      get { return drawingPanel.Handle; }
    }

    /// <summary>
    /// The handle for the overview SFML drawing surface.
    /// </summary>
    public IntPtr OverviewPanelHandle
    {
      get { return overviewPanel.Handle; }
    }

    /// <summary>
    /// Sets the id number of the car being followed.  Needed because the gui 
    /// doesn't know the id of the leader car.
    /// </summary>
    /// <param name="num"></param>
    public void SetFollowingNumber(int num)
    {
      followingLabel.Text = string.Format("Following: Car {0}", num);
    }

    /// <summary>
    /// Set the text indicating the current generation.
    /// </summary>
    /// <param name="generation"></param>
    /// <param name="cars"></param>
    public void NewGeneration(int generation, List<Car.Car> cars)
    {
      Debug.Assert(PopulationSize == cars.Count);

      FollowingCarId = LeaderCarId;
      generationLabel.Text = string.Format(
        "Generation: {0}", generation);

      for (var i = 0; i < PopulationSize; i++)
      {
        var pb = (ColorProgressBar)populationList.Controls[i];
        var car = cars[i];
        car.HealthChanged += SetHealthValue;

        // setting them to max value makes drawing bug?  whatever...
        pb.Value = 99;
        switch (car.Type)
        {
          case EntityType.Normal:
            pb.FillColor = Color.Red;
            break;

          case EntityType.Clone:
            pb.FillColor = Color.Blue;
            break;

          case EntityType.Random:
            pb.FillColor = Color.Magenta;
            break;
        }

        pb.Visible = true;
        pb.Refresh();
      }
    }

    /// <summary>
    /// Set the text indicating the number of live cars.
    /// </summary>
    /// <param name="count"></param>
    public void SetLiveCount(int count)
    {
      liveCountLabel.Text = string.Format("Live Cars: {0}", count);
    }

    /// <summary>
    /// Set the text indicating the distance traveled for the currently 
    /// watched car.
    /// </summary>
    /// <param name="distance"></param>
    public void SetDistance(float distance)
    {
      distanceLabel.Text = string.Format("Distance: {0:F2} m", distance);
    }

    /// <summary>
    /// Add a new champion car to the high score list.
    /// </summary>
    /// <param name="generation"></param>
    /// <param name="id"></param>
    /// <param name="distance"></param>
    public void AddChampion(int generation, int id, float distance)
    {
      m_highScores.Add(new HighScore
      {
        Index = 0,
        Generation = generation,
        Id = id,
        Distance = distance
      });

      m_highScores.Sort();
      highScoreListBox.Items.Clear();
      for (var i = 0; i < m_highScores.Count; i++)
      {
        m_highScores[i].Index = i + 1;
        highScoreListBox.Items.Add(m_highScores[i].DisplayValue);
      }
    }

    /// <summary>
    /// Clear the gui components that aren't regularly updated.
    /// </summary>
    public void ResetUi()
    {
      m_highScores.Clear();
      highScoreListBox.Items.Clear();
    }
    
    private void SetHealthValue(int id, float health)
    {
      var pb = (ColorProgressBar)populationList.Controls[id];
      var value = (int)Math.Round(health * 100);
      pb.Value = Math.Min(value, pb.Maximum);

      if (value <= 0)
      {
        pb.Visible = false;
      }
    }

    private void OnTrackSeedChanged(int seed)
    {
      if (TrackSeedChanged != null)
      {
        TrackSeedChanged(seed);
      }
    }
    
    private void OnPopulationSeedChanged(int seed)
    {
      if (PopulationSeedChanged != null)
      {
        PopulationSeedChanged(seed);
      }
    }

    private void OnPauseSimulation()
    {
      if (PauseSimulation != null)
      {
        PauseSimulation();
      }
    }

    private void OnResumeSimulation()
    {
      if (ResumeSimulation != null)
      {
        ResumeSimulation();
      }
    }

    private void OnNewPopulation()
    {
      if (NewPopulation != null)
      {
        NewPopulation();
      }
    }

    private void OnEnableGraphics()
    {
      if (EnableGraphics != null)
      {
        EnableGraphics();
      }
    }

    private void OnDisableGraphics()
    {
      if (DisableGraphics != null)
      {
        DisableGraphics();
      }
    }

    private void OnKillCar(int id)
    {
      if (KillCar != null)
      {
        KillCar(id);
      }
    }
    
    #region Event Handlers

    private void trackSeedApplyButton_Click(object sender, EventArgs e)
    {
      OnPauseSimulation();

      var str = trackSeedTextBox.Text;
      int seed;
      if (TryGetSeedString(str, out seed))
      {
        var result = MessageBox.Show(
          "Applying a new seed will reset the population.  Continue?",
          "Apply seed?", MessageBoxButtons.YesNo, MessageBoxIcon.Question
          );
        if (result == DialogResult.Yes)
        {
          m_lastTrackSeed = str;
          ResetButtonBackground(trackSeedApplyButton);
          ResetUi();
          OnTrackSeedChanged(seed);
        }
      }

      OnResumeSimulation();
    }

    private void popSeedApplyButton_Click(object sender, EventArgs e)
    {
      OnPauseSimulation();

      var str = popSeedTextBox.Text;
      int seed;
      if (TryGetSeedString(str, out seed))
      {
        var result = MessageBox.Show(
          "Applying a new seed will reset the population.  Continue?",
          "Apply seed?", MessageBoxButtons.YesNo, MessageBoxIcon.Question
          );
        if (result == DialogResult.Yes)
        {
          m_lastPopSeed = str;
          ResetButtonBackground(popSeedApplyButton);
          ResetUi();
          OnPopulationSeedChanged(seed);
        }
      }

      OnResumeSimulation();
    }
    
    private void newPopulationButton_Click(object sender, EventArgs e)
    {
      OnPauseSimulation();

      var result = MessageBox.Show(
        "Discard the current population and start over?", "",
        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
      if (result == DialogResult.Yes)
      {
        ResetUi();
        OnNewPopulation();
      }
      
      OnResumeSimulation();
    }

    private void graphicsButton_Click(object sender, EventArgs e)
    {
      if (m_graphicsEnabled)
      {
        m_graphicsEnabled = false;
        graphicsButton.Text = "Graphics: Off";
        OnDisableGraphics();
      }
      else
      {
        m_graphicsEnabled = true;
        graphicsButton.Text = "Graphics: On";
        OnEnableGraphics();
      }
    }

    private void pauseButton_Click(object sender, EventArgs e)
    {
      if (m_paused)
      {
        pauseButton.Text = "Pause";
        OnResumeSimulation();
        m_paused = false;
      }
      else
      {
        pauseButton.Text = "Resume";
        OnPauseSimulation();
        m_paused = true;
      }
    }

    private void mutationRateTextBox_TextChanged(object sender, EventArgs e)
    {
      var rate = Properties.Settings.Default.MutationRate.ToString();
      var text = mutationRateTextBox.Text;

      if (!text.Equals(rate))
      {
        SetButtonBackgroundRed(mutationRateApplyButton);
      }
      else
      {
        ResetButtonBackground(mutationRateApplyButton);
      }
    }
    
    private void trackSeedTextBox_TextChanged(object sender, EventArgs e)
    {
      var text = trackSeedTextBox.Text;

      if (!text.Equals(m_lastTrackSeed))
      {
        SetButtonBackgroundRed(trackSeedApplyButton);
      }
      else
      {
        ResetButtonBackground(trackSeedApplyButton);
      }
    }

    private void popSeedTextBox_TextChanged(object sender, EventArgs e)
    {
      var text = popSeedTextBox.Text;

      if (!text.Equals(m_lastPopSeed))
      {
        SetButtonBackgroundRed(popSeedApplyButton);
      }
      else
      {
        ResetButtonBackground(popSeedApplyButton);
      }
    }

    private void mutationRateApplyButton_Click(object sender, EventArgs e)
    {
      float rate;
      if (float.TryParse(mutationRateTextBox.Text, out rate) &&
        (rate >= 0 || rate <= 1))
      {
        ResetButtonBackground(mutationRateApplyButton);
        Properties.Settings.Default.MutationRate = rate;
      }
      else
      {
        OnPauseSimulation();
        MessageBox.Show("Invalid mutation rate value.", "Error",
          MessageBoxButtons.OK, MessageBoxIcon.Error);
        OnResumeSimulation();
      }
    }

    private void clonesComboBox_SelectedIndexChanged(object sender, EventArgs e)
    {
      var num = (int)clonesComboBox.SelectedItem;
      Properties.Settings.Default.NumClones = num;
    }
    
    /// <summary>
    /// Draws a combo box with center aligned text.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void ComboBox_DrawItem(object sender, DrawItemEventArgs e)
    {
      // see: http://stackoverflow.com/questions/11817062/align-text-in-combobox

      ComboBox cbx = sender as ComboBox;
      if (cbx != null)
      {
        // Always draw the background
        e.DrawBackground();

        // Drawing one of the items?
        if (e.Index >= 0)
        {
          // Set the string alignment.  Choices are Center, Near and Far
          StringFormat sf = new StringFormat();
          sf.LineAlignment = StringAlignment.Center;
          sf.Alignment = StringAlignment.Center;

          // Set the Brush to ComboBox ForeColor to maintain any ComboBox color 
          // settings
          // Assumes Brush is solid
          Brush brush = new SolidBrush(cbx.ForeColor);

          // If drawing highlighted selection, change brush
          if ((e.State & DrawItemState.Selected) == DrawItemState.Selected)
            brush = SystemBrushes.HighlightText;

          // Draw the string
          e.Graphics.DrawString(
            cbx.Items[e.Index].ToString(), cbx.Font, brush, e.Bounds, sf);
        }
      }
    }

    private void followLeaderButton_Click(object sender, EventArgs e)
    {
      FollowingCarId = LeaderCarId;
    }

    private void randomCarsComboBox_SelectedIndexChanged(object sender,
      EventArgs e)
    {
      var num = (int)randomCarsComboBox.SelectedItem;
      Properties.Settings.Default.NumRandom = num;
    }

    private void functionLoadButton_Click(object sender, EventArgs e)
    {
      OnPauseSimulation();

      var dialog = new OpenFileDialog
      {
        Filter = "lua Files (*.lua)|*.lua|All Files (*.*)|*.*",
        FilterIndex = 0,
        Multiselect = false,
        InitialDirectory = Environment.CurrentDirectory
      };
      var result = dialog.ShowDialog();

      if (result == DialogResult.OK && LuaLoad != null)
      {
        string error;
        if (LuaLoad(dialog.FileName, out error))
        {
          functionsLabel.Text = 
            "Functions: " + Path.GetFileName(dialog.FileName);
        }
        else
        {
          var msg = string.Format("Error loading {0}\n{1}",
            dialog.FileName, error);
          MessageBox.Show(msg, "Error", MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        }
      }

      OnResumeSimulation();
    }
    

    #endregion

    private static void SetButtonBackgroundRed(Button button)
    {
      // paint the button red until the changed are saved
      Bitmap bmp = new Bitmap(button.Width, button.Height);
      using (Graphics g = Graphics.FromImage(bmp))
      {
        Rectangle r = new Rectangle(0, 0, bmp.Width, bmp.Height);
        using (LinearGradientBrush br = new LinearGradientBrush(
          r, Color.Red, Color.DarkRed, LinearGradientMode.Vertical))
        {
          g.FillRectangle(br, r);
        }
      }
      button.ForeColor = Color.White;
      button.BackgroundImage = bmp;
    }

    private static void ResetButtonBackground(Button button)
    {
      button.ForeColor = SystemColors.ControlText;
      button.BackgroundImage = null;
      button.BackColor = SystemColors.Control;
      button.UseVisualStyleBackColor = true;
    }

    private static bool TryGetSeedString(string str, out int seed)
    {
      seed = 0;

      // empty string, prompt to use date/time
      if (string.IsNullOrEmpty(str))
      {
        var result = MessageBox.Show(
          "No seed entered.  Click OK to use the current date/time string.",
          "", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (result != DialogResult.OK)
        {
          return false;
        }

        str = DateTime.Now.ToString("F");
        Log.InfoFormat("Using seed string: {0}", str);
        seed = str.GetHashCode();
        return true;
      }

      // hex string
      if (str.StartsWith(@"\x"))
      {
        str = str.Substring(2);
        const NumberStyles style = NumberStyles.HexNumber;
        var provider = CultureInfo.CurrentCulture;
        if (int.TryParse(str, style, provider, out seed))
        {
          return true;
        }

        MessageBox.Show("Error parsing the hex value.  Seed not changed",
          "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
      }

      // decimal integer string
      if (str.StartsWith(@"\d"))
      {
        str = str.Substring(2);
        const NumberStyles style = NumberStyles.Integer;
        var provider = CultureInfo.CurrentCulture;
        if (int.TryParse(str, style, provider, out seed))
        {
          return true;
        }

        MessageBox.Show("Error parsing the value.  Seed not changed",
          "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
      }

      // just hash the string
      Log.InfoFormat("Using seed string {0}", str);
      seed = str.GetHashCode();
      return true;
    }

    private sealed class HighScore : IComparable
    {
      public int Index { get; set; }
      public int Generation { get; set; }
      public int Id { get; set; }
      public float Distance { get; set; }

      public string DisplayValue
      {
        get { return ToString(); }
      }

      public override string ToString()
      {
        return string.Format("{0}. Gen {1}, {2:F2} m",
          Index, Generation, Distance);
      }

      public int CompareTo(object obj)
      {
        HighScore hs = obj as HighScore;
        if (hs == null)
        {
          return 1;
        }

        if (hs.Distance < Distance)
        {
          return -1;
        }
        else if (hs.Distance == Distance)
        {
          return 0;
        }
        else
        {
          return 1;
        }
      }
    }

    /// <summary>
    /// Simple progress bar that is colorable.
    /// </summary>
    private sealed class ColorProgressBar : ProgressBar
    {
      private SolidBrush m_brush;

      public ColorProgressBar()
      {
        SetStyle(ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        SetStyle(ControlStyles.UserPaint, true);
        FillColor = Color.ForestGreen;
      }

      public int Id { get; set; }

      public Color FillColor
      {
        get { return m_brush.Color; }
        set { m_brush = new SolidBrush(value); }
      }

      protected override void OnPaint(PaintEventArgs e)
      {
        Rectangle rec = e.ClipRectangle;

        rec.Width = (int)(rec.Width * ((double)Value / Maximum)) - 4;
        if (ProgressBarRenderer.IsSupported)
        {
          ProgressBarRenderer.DrawHorizontalBar(e.Graphics, e.ClipRectangle);
        }
        rec.Height = rec.Height - 4;
        e.Graphics.FillRectangle(m_brush, 2, 2, rec.Width, rec.Height);
        
        if (!string.IsNullOrEmpty(Text))
        {
          SizeF len = e.Graphics.MeasureString(Text, Font);
          Point location = 
            new Point(Convert.ToInt32((Width / 2) - len.Width / 2), 
              Convert.ToInt32((Height / 2) - len.Height / 2)
              );
          e.Graphics.DrawString(Text, Font, Brushes.Black, location);
        }
      }
    }
  }
}
