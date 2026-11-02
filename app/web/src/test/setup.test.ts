import { getConfig } from "@testing-library/react";

describe("the text of a failed find", () => {
  it("is built when read, so a poll that fails costs no print of the page", () => {
    document.body.innerHTML = "<p>before</p>";
    const error = getConfig().getElementError("Unable to find it", document.body);
    document.body.innerHTML = "<p>after</p>";
    expect(error.message).toContain("Unable to find it");
    expect(error.message).toContain("after");
    expect(error.message).not.toContain("before");
    expect(error.name).toBe("TestingLibraryElementError");
  });
});
