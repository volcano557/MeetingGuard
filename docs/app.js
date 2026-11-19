const videoPanel = document.querySelector("#project-video");
const video = videoPanel?.querySelector("video");
const playButtons = document.querySelectorAll("[data-play-video]");

for (const button of playButtons) {
  button.addEventListener("click", async () => {
    videoPanel?.scrollIntoView({ behavior: "smooth", block: "center" });

    if (!video) return;

    try {
      await video.play();
    } catch {
      video.focus();
    }
  });
}

if (video) {
  video.addEventListener("play", () => videoPanel?.classList.add("is-playing"));
  video.addEventListener("pause", () => videoPanel?.classList.remove("is-playing"));
}
