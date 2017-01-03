﻿/*
    The MIT License (MIT)

    Copyright (c) 2015 JC Snider, Joe Bridges
  
    Website: http://ascensiongamedev.com
    Contact Email: admin@ascensiongamedev.com

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using IntersectClientExtras.File_Management;
using IntersectClientExtras.Graphics;
using Intersect_Client.Classes.Core;
using Intersect_Client_MonoGame.Classes.SFML.Graphics;
using Intersect_MonoGameDx.Classes.SFML.Audio;

namespace Intersect_Client.Classes.Bridges_and_Interfaces.SFML.File_Management
{
    public class MonoContentManager : GameContentManager
    {
        //Initial Resource Downloading
        private string resourceRelayer = "http://ascensiongamedev.com/resources/Intersect/findResources.php";
        private frmLoadingContent loadingForm;
        private bool downloadCompleted = false;
        private string errorString = "";

        public MonoContentManager()
        {
            if (!Directory.Exists("resources"))
            {
                loadingForm = new frmLoadingContent();
                loadingForm.Show();
                loadingForm.BringToFront();
                using (WebClient client = new WebClient())
                {
                    byte[] response =
                    client.UploadValues(resourceRelayer, new NameValueCollection()
                    {
                       { "version", Assembly.GetExecutingAssembly().GetName().Version.ToString() },
                    });
                    string result = Encoding.UTF8.GetString(response);
                    Uri urlResult;
                    if (Uri.TryCreate(result, UriKind.Absolute, out urlResult))
                    {
                        client.DownloadProgressChanged += Client_DownloadProgressChanged;
                        client.DownloadFileCompleted += Client_DownloadFileCompleted;
                        bool retry = true;
                        while (retry == true)
                        {
                            try
                            {
                                downloadCompleted = false;
                                errorString = "";
                                client.DownloadFileAsync(urlResult, "resources.zip");
                                while (!downloadCompleted)
                                {
                                    Application.DoEvents();
                                }
                            }
                            catch (Exception ex)
                            {
                                errorString = ex.Message;
                            }
                            if (errorString != "")
                            {
                                if (
                                    MessageBox.Show(
                                        "Failed to download client resources.\n\nException Info: " + errorString +
                                        "\n\n" +
                                        "Would you like to try again?", "Failed to load Resources!",
                                        MessageBoxButtons.YesNo) != DialogResult.Yes)
                                {
                                    retry = false;
                                }
                            }
                            else
                            {
                                retry = false;
                            }
                        }
                    }
                    else
                    {
                        MessageBox.Show(
                            "Failed to load resources from client directory and Ascension Game Dev server. Cannot launch game!",
                            "Failed to load Resources!");
                    }
                }
                loadingForm.Close();
            }
            if (!Directory.Exists("resources"))
            {
                Environment.Exit(1);
            }
        }
        private void Client_DownloadFileCompleted(global::System.Object sender, global::System.ComponentModel.AsyncCompletedEventArgs e)
        {
            downloadCompleted = true;
            if (!e.Cancelled && e.Error == null)
            {
                try
                {
                    global::System.IO.Compression.ZipFile.ExtractToDirectory("resources.zip",
                        global::System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
                    File.Delete("resources.zip");
                }
                catch (Exception ex)
                {
                    errorString = ex.Message;
                }
            }
            else
            {
                if (e.Cancelled)
                {
                    errorString = "Download was cancelled!";
                }
                else
                {
                    errorString = e.Error.Message;
                }
            }
        }
        private void Client_DownloadProgressChanged(global::System.Object sender, DownloadProgressChangedEventArgs e)
        {
            loadingForm.SetProgress(e.ProgressPercentage);
        }

        //Graphic Loading
        public override void LoadTilesets(string[] tilesetnames)
        {
            tilesetDict.Clear();
            foreach (var t in tilesetnames)
            {
                if (t != "" && File.Exists(Path.Combine("resources","tilesets",t)))
                {
                   tilesetDict.Add(t.ToLower(),GameGraphics.Renderer.LoadTexture(Path.Combine("resources", "tilesets",t)));
                }
            }
        }

        public void LoadTextureGroup(string directory, Dictionary<string,GameTexture> dict)
        {
            dict.Clear();
            var dir = Path.Combine("resources", directory);
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            var items = Directory.GetFiles(dir, "*.png");
            for (int i = 0; i < items.Length; i++)
            {
                string filename = items[i].Replace(dir, "").TrimStart(Path.DirectorySeparatorChar).ToLower();
                dict.Add(filename, GameGraphics.Renderer.LoadTexture(Path.Combine(dir,filename)));
            }
        }
        public override void LoadItems()
        {
            LoadTextureGroup("items", itemDict);
        }
        public override void LoadEntities()
        {
            LoadTextureGroup("entities", entityDict);
        }
        public override void LoadSpells()
        {
            LoadTextureGroup("spells", spellDict);
        }
        public override void LoadAnimations()
        {
            LoadTextureGroup("animations", animationDict);
        }
        public override void LoadFaces()
        {
            LoadTextureGroup("faces", faceDict);
        }
        public override void LoadImages()
        {
            LoadTextureGroup("images", imageDict);
        }
        public override void LoadFogs()
        {
            LoadTextureGroup("fogs", fogDict);
        }
        public override void LoadResources()
        {
            LoadTextureGroup("resources", resourceDict);
        }
        public override void LoadPaperdolls()
        {
            LoadTextureGroup("paperdolls", paperdollDict);
        }
        public override void LoadGui()
        {
            LoadTextureGroup("gui", guiDict);
        }
        public override void LoadMisc()
        {
            LoadTextureGroup("misc", miscDict);
        }

        public override void LoadFonts()
        {
            fontDict.Clear();
            var dir = Path.Combine("resources", "fonts");
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            var items = Directory.GetFiles(dir, "*.xnb");
            for (int i = 0; i < items.Length; i++)
            {
                string filename = items[i].Replace(dir, "").TrimStart(Path.DirectorySeparatorChar).ToLower();
                GameFont font = GameGraphics.Renderer.LoadFont(Path.Combine(dir,filename));
                if (fontDict.IndexOf(font) == -1)
                    fontDict.Add(font);
            }
        }

        public override void LoadShaders()
        {
            shaderDict.Clear();
            var dir = Path.Combine("resources", "shaders");
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            var items = Directory.GetFiles(dir, "*.xnb");
            for (int i = 0; i < items.Length; i++)
            {
                string filename = items[i].Replace(dir, "").TrimStart(Path.DirectorySeparatorChar).ToLower();
                shaderDict.Add(filename.Replace(".xnb",""), GameGraphics.Renderer.LoadShader(Path.Combine(dir,filename)));
            }
        }

        public override void LoadSounds()
        {
            soundDict.Clear();
            var dir = Path.Combine("resources", "sounds");
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            var items = Directory.GetFiles(dir, "*.wav");
            for (int i = 0; i < items.Length; i++)
            {
                string filename = items[i].Replace(dir, "").TrimStart(Path.DirectorySeparatorChar).ToLower();
                soundDict.Add(RemoveExtension(filename), new MonoSoundSource(Path.Combine(dir,filename), ((MonoRenderer)GameGraphics.Renderer).GetContentManager()));
            }
        }

        public override void LoadMusic()
        {
            musicDict.Clear();
            var dir = Path.Combine("resources", "music");
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            var items = Directory.GetFiles(dir, "*.ogg");
            for (int i = 0; i < items.Length; i++)
            {
                string filename =items[i].Replace(dir, "").TrimStart(Path.DirectorySeparatorChar).ToLower();
                musicDict.Add(RemoveExtension(filename), new MonoMusicSource(Path.Combine(dir,filename)));
            }
        }
    }
}